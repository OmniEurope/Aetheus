// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Users;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// TOCTOU regression coverage: <c>UserService</c> must commit the role/activation change via
/// <c>SaveChangesAsync</c> BEFORE invalidating the authz caches (<c>IResourceAuthorizationService
/// .InvalidateRoleCache</c>). Evicting first opens a window where a concurrent read between the
/// eviction and the commit re-populates the cache with the OLD role set for the remainder of its
/// TTL (~5 min) - a stale-privilege bug. These tests assert the call order via NSubstitute's
/// <c>Received.InOrder</c>, which a regression to the old ordering would fail.
/// </summary>
public class UserServiceCacheInvalidationOrderTests
{
    private readonly IUserRepository _repoMock = Substitute.For<IUserRepository>();
    private readonly IHttpContextAccessor _httpContextAccessorMock = Substitute.For<IHttpContextAccessor>();
    private readonly IAdminChangeNotifier _notifier = Substitute.For<IAdminChangeNotifier>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IUserChangeNotifier _userNotifier = Substitute.For<IUserChangeNotifier>();
    private readonly UserService _sut;

    public UserServiceCacheInvalidationOrderTests()
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
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>());
    }

    [Fact]
    public async Task UpdateUserAsync_RoleChange_InvalidatesAuthzCache_AfterSaveChangesCommits()
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

        Received.InOrder(() =>
        {
            _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>());
            _authz.InvalidateRoleCache("u");
        });
    }

    [Fact]
    public async Task AssignRoleAsync_InvalidatesAuthzCache_AfterSaveChangesCommits()
    {
        var user = new User { Id = 1, Username = "u", IsActive = true, SecurityStamp = "old", UserRoles = [] };
        _repoMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.FindRoleAsync(2, Arg.Any<CancellationToken>()).Returns(new Role { Id = 2, Name = "Dev" });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AssignRoleAsync(1, 2, ct: TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>());
            _authz.InvalidateRoleCache("u");
        });
    }

    [Fact]
    public async Task ChangePasswordAsync_InvalidatesAuthzCache_AfterSaveChangesCommits()
    {
        // No HttpContext => system reset, which bypasses the current-password check and reaches the
        // save + eviction. Rotating the security stamp then evicting BEFORE the commit is the TOCTOU this
        // guards - the eviction must run AFTER SaveChangesAsync.
        var user = new User
        {
            Id = 1,
            Username = "u",
            Email = "u@test.com",
            IsActive = true,
            SecurityStamp = "old",
            PasswordHash = "irrelevant-for-system-reset"
        };
        _repoMock.FindUserAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        // Explicitly null the HttpContext (NSubstitute would otherwise auto-mock it non-null, making
        // isSystem false and short-circuiting the reset).
        _httpContextAccessorMock.HttpContext.Returns((HttpContext?)null);

        var ok = await _sut.ChangePasswordAsync(1, new ChangeUserPasswordRequest { NewPassword = "NewPassw0rd!" }, ct: TestContext.Current.CancellationToken);

        Assert.True(ok);
        Received.InOrder(() =>
        {
            _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>());
            _authz.InvalidateRoleCache("u");
        });
    }

    [Fact]
    public async Task UnassignRoleAsync_InvalidatesAuthzCache_AfterSaveChangesCommits()
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

        await _sut.UnassignRoleAsync(1, 2, ct: TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>());
            _authz.InvalidateRoleCache("u");
        });
    }
}
