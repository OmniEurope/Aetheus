// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Users;
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class UserServiceTests
{
    private readonly IUserRepository _repoMock = Substitute.For<IUserRepository>();
    private readonly IHttpContextAccessor _httpContextAccessorMock = Substitute.For<IHttpContextAccessor>();
    private readonly IAdminChangeNotifier _notifier = Substitute.For<IAdminChangeNotifier>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IUserChangeNotifier _userNotifier = Substitute.For<IUserChangeNotifier>();
    private readonly IDomainEventDispatcher _domainEvents = Substitute.For<IDomainEventDispatcher>();
    private readonly UserService _sut;

    public UserServiceTests()
    {
        _sut = new UserService(
            _repoMock,
            _httpContextAccessorMock,
            Substitute.For<IAuditService>(),
                _authz,
                new MemoryCache(new MemoryCacheOptions()),
                TimeProvider.System,
                _notifier,
                _userNotifier,
                _domainEvents,
                _roleAssignments);
    }

    private readonly Aetheus.Back.Services.IOrganizationRoleAssignmentService _roleAssignments =
        Substitute.For<Aetheus.Back.Services.IOrganizationRoleAssignmentService>();

    [Fact]
    public async Task GetUsersAsync_ReturnsMappedPaginatedResult()
    {
        var userDtos = new List<UserDto>
        {
            new()
            {
                Id = 1, Username = "admin", Email = "a@b.com", IsActive = true,
                Roles = ["Admin"]
            }
        };
        _repoMock.GetUsersPagedProjectedAsync(null, 1, 10, Arg.Any<CancellationToken>())
            .Returns((userDtos, 1));

        var result = await _sut.GetUsersAsync(new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("admin", result.Items[0].Username);
        Assert.Contains("Admin", result.Items[0].Roles);
    }

    [Fact]
    public async Task GetUsersAsync_EmptyList_ReturnsEmpty()
    {
        _repoMock.GetUsersPagedProjectedAsync(null, 1, 10, Arg.Any<CancellationToken>())
            .Returns((new List<UserDto>(), 0));

        var result = await _sut.GetUsersAsync(new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetUserDetailAsync_Found_ReturnsDetail()
    {
        var user = new User
        {
            Id = 1,
            Username = "admin",
            Email = "a@b.com",
            IsActive = true,
            UserRoles = [new UserRole { Role = new Role { Name = "Admin" } }]
        };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _sut.GetUserDetailAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("admin", result.Username);
        Assert.Contains("Admin", result.Roles);
    }

    [Fact]
    public async Task GetUserDetailAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetUserDetailAsync(99, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _sut.GetUserDetailAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateUserAsync_Success_ReturnsDto()
    {
        _repoMock.FindByUsernameAsync("newuser", Arg.Any<CancellationToken>())
            .Returns((User?)null);
        _repoMock.AddUserAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetRolesByNamesAsync(Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns([new Role { Id = 1, Name = "Admin" }]);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetUserDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = 1,
                Username = "newuser",
                Email = "new@test.com",
                IsActive = true,
                UserRoles = [new UserRole { Role = new Role { Name = "Admin" } }]
            });

        var result = await _sut.CreateUserAsync(new CreateUserRequest
        {
            Username = "newuser",
            Email = "new@test.com",
            Roles = ["Admin"]
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("newuser", result.Username);
        Assert.Contains("Admin", result.Roles);
        await _repoMock.Received(1).AddUserAsync(Arg.Is<User>(u => u.Username == "newuser"), Arg.Any<CancellationToken>());
    }

    private void ArrangeCreation()
    {
        _repoMock.FindByUsernameAsync("newuser", Arg.Any<CancellationToken>()).Returns((User?)null);
        _repoMock.GetUserDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "newuser", Email = "new@test.com", IsActive = true });
    }

    private static CreateUserRequest NewUser() =>
        new() { Username = "newuser", Email = "new@test.com" };

    [Fact]
    public async Task CreateUserAsync_RequestsTheWelcomeMail_AndAnswersWhatBecameOfIt()
    {
        ArrangeCreation();
        var sent = new WelcomeMailResultDto { Status = WelcomeMailStatus.Sent, To = "new@test.com", Message = "Accepted" };
        await _domainEvents.DispatchAsync(
            Arg.Do<UserWelcomeRequestedEvent>(welcome => welcome.Outcome = sent), Arg.Any<CancellationToken>());

        var result = await _sut.CreateUserAsync(NewUser(), ct: TestContext.Current.CancellationToken);

        await _domainEvents.Received(1).DispatchAsync(
            Arg.Is<UserWelcomeRequestedEvent>(welcome => welcome.Email == "new@test.com" && welcome.Username == "newuser"),
            Arg.Any<CancellationToken>());
        Assert.Same(sent, result.WelcomeMail);
    }

    [Fact]
    public async Task CreateUserAsync_WhenNoOneSentTheWelcomeMail_StillCreates_AndSaysItDidNotLeave()
    {
        ArrangeCreation();

        var result = await _sut.CreateUserAsync(NewUser(), ct: TestContext.Current.CancellationToken);

        Assert.Equal("newuser", result.Username);
        await _repoMock.Received(1).AddUserAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        Assert.Equal(WelcomeMailStatus.Failed, result.WelcomeMail!.Status);
        Assert.Equal("new@test.com", result.WelcomeMail.To);
    }

    [Fact]
    public async Task CreateUserAsync_DuplicateUsername_ThrowsConflict()
    {
        _repoMock.FindByUsernameAsync("existing", Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "existing" });

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.CreateUserAsync(new CreateUserRequest
            {
                Username = "existing",
            }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    // R-14 (owner decision 2026-10-28): an account is created with a password nobody knows; the welcome
    // link is the only way in, so two creations never share a hash and no forced change is asked.
    public async Task CreateUserAsync_StoresAPasswordNobodyKnows_AndForcesNoChange()
    {
        _repoMock.FindByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);
        var captured = new List<User>();
        _repoMock.AddUserAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => captured.Add(ci.Arg<User>()));
        _repoMock.GetUserDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "u", UserRoles = [] });

        await _sut.CreateUserAsync(new CreateUserRequest { Username = "u", Email = "u@test.com" }, ct: TestContext.Current.CancellationToken);
        await _sut.CreateUserAsync(new CreateUserRequest { Username = "v", Email = "v@test.com" }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, captured.Count);
        Assert.All(captured, u =>
        {
            Assert.StartsWith("$2", u.PasswordHash, StringComparison.Ordinal);
            Assert.False(BCrypt.Net.BCrypt.Verify(string.Empty, u.PasswordHash));
            Assert.False(u.MustChangePassword);
        });
        Assert.NotEqual(captured[0].PasswordHash, captured[1].PasswordHash);
    }

    // PLAN-003 P3-2: the e-mail is the sign-in identifier, stored normalized and held by one account.
    [Fact]
    public async Task CreateUserAsync_StoresTheEmailTrimmedAndLowerCase()
    {
        User? capturedUser = null;
        _repoMock.AddUserAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => { capturedUser = ci.Arg<User>(); });
        _repoMock.GetUserDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "u", UserRoles = [] });

        await _sut.CreateUserAsync(new CreateUserRequest { Username = "u", Email = "  Mixed@Test.COM " },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("mixed@test.com", capturedUser!.Email);
        await _repoMock.Received(1).EmailInUseAsync("mixed@test.com", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUserAsync_EmailHeldByAnotherAccount_ThrowsConflictWithItsCode_AndAddsNothing()
    {
        _repoMock.EmailInUseAsync("taken@test.com", null, Arg.Any<CancellationToken>()).Returns(true);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => _sut.CreateUserAsync(
            new CreateUserRequest { Username = "u", Email = "Taken@Test.com" },
            ct: TestContext.Current.CancellationToken));

        Assert.Equal(UserErrorCodes.EmailInUse, refusal.Code);
        await _repoMock.DidNotReceive().AddUserAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task CreateUserAsync_WithoutEmail_IsRefused_AndAddsNothing(string? email)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateUserAsync(
            new CreateUserRequest { Username = "u", Email = email },
            ct: TestContext.Current.CancellationToken));

        await _repoMock.DidNotReceive().AddUserAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateUserAsync_EmailHeldByAnotherAccount_ThrowsConflict_AndSavesNothing()
    {
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "u", Email = "u@test.com", IsActive = true, UserRoles = [] });
        _repoMock.EmailInUseAsync("taken@test.com", 1, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.UpdateUserAsync(1,
            new UpdateUserRequest { Username = "u", Email = "TAKEN@test.com", IsActive = true },
            ct: TestContext.Current.CancellationToken));

        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateUserAsync_Found_UpdatesAndReturns()
    {
        var user = new User
        {
            Id = 1,
            Username = "old",
            Email = "old@test.com",
            IsActive = true,
            UserRoles = new List<UserRole>()
        };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(user);
        _repoMock.GetRolesByNamesAsync(Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns([new Role { Id = 1, Name = "Admin" }]);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.UpdateUserAsync(1, new UpdateUserRequest
        {
            Username = "updated",
            Email = "new@test.com",
            IsActive = false,
            Roles = ["Admin"]
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("updated", result.Username);
        Assert.Equal("new@test.com", result.Email);
        Assert.False(result.IsActive);
    }

    [Fact]
    public async Task UpdateUserAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetUserDetailAsync(99, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _sut.UpdateUserAsync(99, new UpdateUserRequest { Username = "x" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateUserAsync_DuplicateUsername_ThrowsConflict()
    {
        var user = new User
        {
            Id = 1,
            Username = "user1",
            UserRoles = new List<UserRole>()
        };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(user);
        _repoMock.FindByUsernameAsync("taken", Arg.Any<CancellationToken>())
            .Returns(new User { Id = 2, Username = "taken" });

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.UpdateUserAsync(1, new UpdateUserRequest { Username = "taken" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteUserAsync_Found_ReturnsTrue()
    {
        var user = new User { Id = 1, Username = "todelete" };
        _repoMock.FindUserAsync(1, Arg.Any<CancellationToken>())
            .Returns(user);
        _repoMock.RemoveUserAsync(user, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.DeleteUserAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).RemoveUserAsync(user, Arg.Any<CancellationToken>());
        // A deleted user's session is over, so what the modules hold in memory for them goes too.
        await _domainEvents.Received(1).DispatchAsync(
            new UserSessionEndedEvent(1, UserSessionEndReasons.Deleted), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUserAsync_NotFound_ReturnsFalse()
    {
        _repoMock.FindUserAsync(99, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _sut.DeleteUserAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ChangePasswordAsync_Found_HashesAndReturnsTrue()
    {
        // Explicitly return null HttpContext so the service treats this as a system-driven reset.
        _httpContextAccessorMock.HttpContext.Returns((HttpContext?)null);

        var user = new User { Id = 1, Username = "user", PasswordHash = "oldhash" };
        _repoMock.FindUserAsync(1, Arg.Any<CancellationToken>())
            .Returns(user);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.ChangePasswordAsync(1, new ChangeUserPasswordRequest { NewPassword = "newpass123" }, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.True(BCrypt.Net.BCrypt.Verify("newpass123", user.PasswordHash));
    }

    [Fact]
    public async Task ChangePasswordAsync_NotFound_ReturnsFalse()
    {
        _repoMock.FindUserAsync(99, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _sut.ChangePasswordAsync(99, new ChangeUserPasswordRequest { NewPassword = "pass" }, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_SelfWithCorrectCurrentPassword_ReturnsTrue()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "user")], "test"));
        _httpContextAccessorMock.HttpContext.Returns(new DefaultHttpContext { User = principal });

        var user = new User { Id = 1, Username = "user", PasswordHash = BCrypt.Net.BCrypt.HashPassword("oldpass") };
        _repoMock.FindByUsernameAsync("user", Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.FindUserAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.ChangeOwnPasswordAsync(
            new ChangeUserPasswordRequest { CurrentPassword = "oldpass", NewPassword = "newpass123" }, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.True(BCrypt.Net.BCrypt.Verify("newpass123", user.PasswordHash));
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_WrongCurrentPassword_ReturnsFalse()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "user")], "test"));
        _httpContextAccessorMock.HttpContext.Returns(new DefaultHttpContext { User = principal });

        var user = new User { Id = 1, Username = "user", PasswordHash = BCrypt.Net.BCrypt.HashPassword("oldpass") };
        _repoMock.FindByUsernameAsync("user", Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.FindUserAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.ChangeOwnPasswordAsync(
            new ChangeUserPasswordRequest { CurrentPassword = "wrong", NewPassword = "newpass123" }, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_NoAuthenticatedUser_ReturnsFalse()
    {
        _httpContextAccessorMock.HttpContext.Returns((HttpContext?)null);

        var result = await _sut.ChangeOwnPasswordAsync(
            new ChangeUserPasswordRequest { CurrentPassword = "x", NewPassword = "newpass123" }, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        await _repoMock.DidNotReceive().FindByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // F16: creating a user must broadcast an admin "User Created" change so admin lists refresh live.
    [Fact]
    public async Task CreateUserAsync_BroadcastsTheCreation()
    {
        ArrangeCreation();

        await _sut.CreateUserAsync(NewUser(), ct: TestContext.Current.CancellationToken);

        await _notifier.Received(1).BroadcastAsync(AdminEntities.User, Arg.Any<int>(), EntityChangeOps.Created, Arg.Any<CancellationToken>());
    }

    // --- MustChangePassword flag (S-FEAT-UE4K) ---

    [Fact]
    public async Task UpdateUserAsync_SetsMustChangePasswordTrueFromRequest()
    {
        var user = new User
        {
            Id = 1,
            Username = "u",
            Email = "u@test.com",
            IsActive = true,
            MustChangePassword = false,
            UserRoles = new List<UserRole>()
        };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.UpdateUserAsync(1, new UpdateUserRequest
        {
            Username = "u",
            Email = "u@test.com",
            IsActive = true,
            MustChangePassword = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result.MustChangePassword);
        Assert.True(user.MustChangePassword);
        // F16: updating a user must broadcast an admin "User Updated" change.
        await _notifier.Received(1).BroadcastAsync(AdminEntities.User, 1, EntityChangeOps.Updated, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateUserAsync_ClearsMustChangePasswordWhenRequestFalse()
    {
        var user = new User
        {
            Id = 1,
            Username = "u",
            Email = "u@test.com",
            IsActive = true,
            MustChangePassword = true,
            UserRoles = new List<UserRole>()
        };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.UpdateUserAsync(1, new UpdateUserRequest
        {
            Username = "u",
            Email = "u@test.com",
            IsActive = true,
            MustChangePassword = false
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.MustChangePassword);
        Assert.False(user.MustChangePassword);
    }

    [Fact]
    public async Task ChangePasswordAsync_AdminResetOfOtherUser_ReArmsMustChangePassword()
    {
        // Admin (not self) resetting another user's password re-arms the forced-change requirement.
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "admin"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")
            ], "test"));
        _httpContextAccessorMock.HttpContext.Returns(new DefaultHttpContext { User = principal });

        var user = new User
        {
            Id = 7,
            Username = "target",
            PasswordHash = "oldhash",
            MustChangePassword = false
        };
        _repoMock.FindUserAsync(7, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.ChangePasswordAsync(7, new ChangeUserPasswordRequest { NewPassword = "newpass123" }, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.True(user.MustChangePassword);
        Assert.True(BCrypt.Net.BCrypt.Verify("newpass123", user.PasswordHash));
        // An admin reset does not end the target's session (its refresh token stays valid).
        await _domainEvents.DidNotReceive().DispatchAsync(
            Arg.Any<UserSessionEndedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangePasswordAsync_SelfChange_ClearsMustChangePassword()
    {
        // A self-service change satisfies the pending forced-change requirement and clears the flag.
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "self")], "test"));
        _httpContextAccessorMock.HttpContext.Returns(new DefaultHttpContext { User = principal });

        var user = new User
        {
            Id = 3,
            Username = "self",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("oldpass"),
            MustChangePassword = true
        };
        _repoMock.FindUserAsync(3, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.ChangePasswordAsync(3,
            new ChangeUserPasswordRequest { CurrentPassword = "oldpass", NewPassword = "newpass123" }, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.False(user.MustChangePassword);
        // The client signs out right after, with a token the server already refuses.
        await _domainEvents.Received(1).DispatchAsync(
            new UserSessionEndedEvent(3, UserSessionEndReasons.PasswordChanged), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangePasswordAsync_NonAdminOnAnotherUser_IsRefusedAndChangesNothing()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "someone")], "test"));
        _httpContextAccessorMock.HttpContext.Returns(new DefaultHttpContext { User = principal });
        var user = new User { Id = 8, Username = "target", PasswordHash = "oldhash" };
        _repoMock.FindUserAsync(8, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.ChangePasswordAsync(8, new ChangeUserPasswordRequest { NewPassword = "newpass123" }, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal("oldhash", user.PasswordHash);
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrongpass")]
    public async Task ChangePasswordAsync_SelfWithoutTheRightCurrentPassword_IsRefused(string? currentPassword)
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "self")], "test"));
        _httpContextAccessorMock.HttpContext.Returns(new DefaultHttpContext { User = principal });
        var oldHash = BCrypt.Net.BCrypt.HashPassword("oldpass");
        var user = new User { Id = 3, Username = "self", PasswordHash = oldHash, MustChangePassword = true };
        _repoMock.FindUserAsync(3, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.ChangePasswordAsync(3,
            new ChangeUserPasswordRequest { CurrentPassword = currentPassword, NewPassword = "newpass123" }, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(oldHash, user.PasswordHash);
        Assert.True(user.MustChangePassword);
    }

    [Fact]
    public async Task GetRolesAsync_ReturnsSortedRoleNames()
    {
        _repoMock.GetAllRoleNamesAsync(Arg.Any<CancellationToken>())
            .Returns(["Admin", "User"]);

        var result = await _sut.GetRolesAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("Admin", result[0]);
        Assert.Equal("User", result[1]);
    }

    // --- Role assignment (S-FEAT admin pages + realtime) ---

    [Fact]
    public async Task UpdateUserAsync_RoleChange_RotatesStampInvalidatesCacheAndNotifies()
    {
        var user = new User
        {
            Id = 5,
            Username = "u",
            Email = "u@test.com",
            IsActive = true,
            SecurityStamp = "old",
            UserRoles = new List<UserRole>()
        };
        _repoMock.GetUserDetailAsync(5, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.GetRolesByNamesAsync(Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns([new Role { Id = 1, Name = "Admin" }]);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpdateUserAsync(5, new UpdateUserRequest
        {
            Username = "u",
            Email = "u@test.com",
            IsActive = true,
            Roles = ["Admin"]
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("old", user.SecurityStamp);
        _authz.Received(1).InvalidateRoleCache("u");
        await _userNotifier.Received(1).NotifyPermissionsChangedAsync(5, PermissionChangeReasons.Roles, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignRoleAsync_UserNotFound_ThrowsNotFound()
    {
        _repoMock.GetUserDetailAsync(99, Arg.Any<CancellationToken>()).Returns((User?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.AssignRoleAsync(99, 1, null, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssignRoleAsync_RoleNotFound_ThrowsNotFound()
    {
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "u", UserRoles = [] });
        _repoMock.FindRoleAsync(99, Arg.Any<CancellationToken>()).Returns((Role?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.AssignRoleAsync(1, 99, null, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssignRoleAsync_AlreadyHasRole_ThrowsConflict()
    {
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "u", UserRoles = [] });
        _repoMock.FindRoleAsync(2, Arg.Any<CancellationToken>()).Returns(new Role { Id = 2, Name = "Dev" });
        _roleAssignments.AssignAsync(1, 4, 2, Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.AssignRoleAsync(1, 2, 4, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssignRoleAsync_Valid_AddsRoleForTheOrganizationRotatesStampAndNotifies()
    {
        var user = new User { Id = 1, Username = "u", IsActive = true, SecurityStamp = "old", UserRoles = [] };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.FindRoleAsync(2, Arg.Any<CancellationToken>()).Returns(new Role { Id = 2, Name = "Dev" });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _roleAssignments.AssignAsync(1, 4, 2, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.AssignRoleAsync(1, 2, 4, ct: TestContext.Current.CancellationToken);

        // R-47: the role is held for organization 4, never site-wide.
        await _roleAssignments.Received(1).AssignAsync(1, 4, 2, Arg.Any<CancellationToken>());
        Assert.Empty(user.UserRoles);
        Assert.NotEqual("old", user.SecurityStamp);
        _authz.Received(1).InvalidateRoleCache("u");
        await _userNotifier.Received(1).NotifyPermissionsChangedAsync(1, PermissionChangeReasons.Roles, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignRoleAsync_OrganizationRoleWithoutOrganization_ThrowsBadRequest()
    {
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "u", UserRoles = [] });
        _repoMock.FindRoleAsync(2, Arg.Any<CancellationToken>()).Returns(new Role { Id = 2, Name = "Dev" });

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.AssignRoleAsync(1, 2, null, ct: TestContext.Current.CancellationToken));
        await _roleAssignments.DidNotReceive().AssignAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignRoleAsync_Admin_IsHeldSiteWide()
    {
        var user = new User { Id = 1, Username = "u", IsActive = true, SecurityStamp = "old", UserRoles = [] };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.FindRoleAsync(9, Arg.Any<CancellationToken>()).Returns(new Role { Id = 9, Name = "Admin" });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AssignRoleAsync(1, 9, null, ct: TestContext.Current.CancellationToken);

        Assert.Contains(user.UserRoles, ur => ur.RoleId == 9);
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.AssignRoleAsync(1, 9, 4, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnassignRoleAsync_NotInRole_ReturnsFalse()
    {
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "u", UserRoles = [] });

        var result = await _sut.UnassignRoleAsync(1, 2, null, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task UnassignRoleAsync_LastActiveAdmin_ThrowsBadRequest()
    {
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = 1,
                Username = "admin",
                IsActive = true,
                UserRoles = [new UserRole { RoleId = 9, Role = new Role { Id = 9, Name = "Admin" } }]
            });
        _repoMock.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(1);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UnassignRoleAsync(1, 9, null, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnassignRoleAsync_Valid_RemovesRoleAndNotifies()
    {
        var user = new User
        {
            Id = 1,
            Username = "u",
            IsActive = true,
            SecurityStamp = "old",
            UserRoles = [new UserRole { RoleId = 2, Role = new Role { Id = 2, Name = "Dev" } }]
        };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.UnassignRoleAsync(1, 2, null, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Empty(user.UserRoles);
        Assert.NotEqual("old", user.SecurityStamp);
        await _userNotifier.Received(1).NotifyPermissionsChangedAsync(1, PermissionChangeReasons.Roles, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateUserAsync_RemovingAdminFromLastActiveAdmin_ThrowsBadRequest()
    {
        // The bulk edit path (Roles tab "Save") must enforce the same last-admin guard as UnassignRoleAsync.
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = 1,
                Username = "admin",
                Email = "a@b.com",
                IsActive = true,
                UserRoles = [new UserRole { RoleId = 9, Role = new Role { Id = 9, Name = "Admin" } }]
            });
        _repoMock.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(1);

        var request = new UpdateUserRequest
        {
            Username = "admin",
            Email = "a@b.com",
            IsActive = true,
            Roles = ["User"]
        };

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdateUserAsync(1, request, ct: TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateUserAsync_SelfDeactivation_WithAnotherAdmin_IsRefused()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "admin")], "test"));
        _httpContextAccessorMock.HttpContext.Returns(new DefaultHttpContext { User = principal });

        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = 1,
                Username = "admin",
                Email = "a@b.com",
                IsActive = true,
                UserRoles = [new UserRole { RoleId = 9, Role = new Role { Id = 9, Name = "Admin" } }]
            });
        _repoMock.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(2);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var request = new UpdateUserRequest
        {
            Username = "admin",
            Email = "a@b.com",
            IsActive = false,
            Roles = ["Admin"]
        };

        // R-25: an account is never switched off by its own holder, even when another administrator remains.
        await Assert.ThrowsAsync<BadRequestException>(
            () => _sut.UpdateUserAsync(1, request, ct: TestContext.Current.CancellationToken));

        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateUserAsync_SelfDeactivation_AsLastActiveAdmin_ThrowsBadRequest()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "admin")], "test"));
        _httpContextAccessorMock.HttpContext.Returns(new DefaultHttpContext { User = principal });
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>()).Returns(new User
        {
            Id = 1,
            Username = "admin",
            IsActive = true,
            UserRoles = [new UserRole { RoleId = 9, Role = new Role { Id = 9, Name = "Admin" } }]
        });
        _repoMock.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(1);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdateUserAsync(1,
            new UpdateUserRequest
            {
                Username = "admin",
                IsActive = false,
                Roles = ["Admin"]
            }, ct: TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // --- Audit AUD-01 to AUD-03 (recette R1-50): sessions end with the identity they were opened on ---

    [Fact]
    public async Task ChangePasswordAsync_RevokesEveryRefreshToken()
    {
        _httpContextAccessorMock.HttpContext.Returns((HttpContext?)null);
        var user = new User { Id = 1, Username = "user", PasswordHash = "oldhash" };
        _repoMock.FindUserAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        Assert.True(await _sut.ChangePasswordAsync(
            1, new ChangeUserPasswordRequest { NewPassword = "newpass123" }, TestContext.Current.CancellationToken));

        await _repoMock.Received(1).RevokeRefreshTokensAsync(1, user.UpdatedAt, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("renamed", false)]
    [InlineData("u", true)]
    public async Task UpdateUserAsync_RenameOrArmedForcedChange_RotatesTheSecurityStamp(string username, bool mustChange)
    {
        var user = new User
        {
            Id = 1,
            Username = "u",
            IsActive = true,
            MustChangePassword = false,
            SecurityStamp = "old-stamp",
            UserRoles = []
        };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        await _sut.UpdateUserAsync(1, new UpdateUserRequest
        {
            Username = username,
            Email = "u@test.com",
            IsActive = true,
            MustChangePassword = mustChange,
            Roles = []
        }, TestContext.Current.CancellationToken);

        Assert.NotEqual("old-stamp", user.SecurityStamp);
    }

    [Fact]
    public async Task UpdateUserAsync_WithoutSecurityChange_KeepsTheSecurityStamp()
    {
        var user = new User
        {
            Id = 1,
            Username = "u",
            Email = "old@test.com",
            IsActive = true,
            SecurityStamp = "old-stamp",
            UserRoles = []
        };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        await _sut.UpdateUserAsync(1, new UpdateUserRequest
        {
            Username = "u",
            Email = "new@test.com",
            IsActive = true,
            Roles = []
        }, TestContext.Current.CancellationToken);

        Assert.Equal("old-stamp", user.SecurityStamp);
    }
}
