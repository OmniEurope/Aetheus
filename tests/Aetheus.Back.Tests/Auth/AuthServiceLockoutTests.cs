// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

public class AuthServiceLockoutTests
{
    private const string Password = "Correct-Password-1!";
    private static readonly string PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password);

    private readonly IAuthRepository _repo = Substitute.For<IAuthRepository>();
    private readonly TimeProvider _time = Substitute.For<TimeProvider>();
    private readonly AuthService _sut;

    public AuthServiceLockoutTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:AdminPassword"] = "admin",
                ["Auth:JwtKey"] = "aetheus-dev-key-minimum-32-bytes!!"
            })
            .Build();

        var orgRepo = Substitute.For<IOrganizationService>();
        orgRepo.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>()).Returns(1);

        // Default: "now" is a fixed instant in 2025
        _time.GetUtcNow().Returns(new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));

        // DB users exist so the fallback admin path is skipped
        _repo.AnyUsersExistAsync(Arg.Any<CancellationToken>()).Returns(true);

        _sut = new AuthService(
            _repo,
            config,
            Substitute.For<IAuditService>(),
            new JwtOptions { SigningKey = "aetheus-dev-key-minimum-32-bytes!!" },
            Substitute.For<IMemoryCache>(),
            orgRepo,
            Substitute.For<ITotpService>(),
            _time,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<Aetheus.Back.Services.IAdminChangeNotifier>(),
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(), Substitute.For<ILogger<AuthService>>());
    }

    private User MakeUser(int failedCount = 0, DateTime? lockoutEnd = null) => new()
    {
        Id = 1,
        Username = "testuser",
        Email = "testuser@example.com",
        PasswordHash = PasswordHash,
        IsActive = true,
        FailedLoginCount = failedCount,
        LockoutEndUtc = lockoutEnd,
        SecurityStamp = "stamp",
        UserRoles = [new UserRole { Role = new Role { Name = "User" } }]
    };

    [Fact]
    public async Task LoginAsync_FailedAttempts_IncrementsCount()
    {
        var user = MakeUser(failedCount: 0);
        _repo.FindUserByEmailWithRolesAsync("testuser@example.com", Arg.Any<CancellationToken>()).Returns(user);

        // 5 failed login attempts with wrong password
        for (var i = 0; i < 5; i++)
        {
            await _sut.LoginAsync(new LoginRequest { Email = "testuser@example.com", Password = "wrong" }, ct: TestContext.Current.CancellationToken);
        }

        // IncrementFailedLoginAsync must have been called 5 times
        await _repo.Received(5).IncrementFailedLoginAsync(user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_MaxFailedAttempts_LocksOutUser()
    {
        // User already has 4 failed attempts - the next failure is the 5th (MaxFailedAttempts)
        var user = MakeUser(failedCount: 4);
        _repo.FindUserByEmailWithRolesAsync("testuser@example.com", Arg.Any<CancellationToken>()).Returns(user);
        _repo.IncrementFailedLoginAsync(user.Id, Arg.Any<CancellationToken>()).Returns(5);

        var result = await _sut.LoginAsync(new LoginRequest { Email = "testuser@example.com", Password = "wrong" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repo.Received(1).IncrementFailedLoginAsync(user.Id, Arg.Any<CancellationToken>());
        await _repo.Received(1).LockoutUserAsync(user.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_ConcurrentFailuresReachTheThreshold_LocksOutFromTheStoredCount()
    {
        // Every concurrent request read the same snapshot (0 failures); the store holds all five.
        var user = MakeUser(failedCount: 0);
        _repo.FindUserByEmailWithRolesAsync("testuser@example.com", Arg.Any<CancellationToken>()).Returns(user);
        _repo.IncrementFailedLoginAsync(user.Id, Arg.Any<CancellationToken>()).Returns(5);

        var result = await _sut.LoginAsync(new LoginRequest { Email = "testuser@example.com", Password = "wrong" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repo.Received(1).LockoutUserAsync(user.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_LockedOut_RejectsBeforePasswordCheck()
    {
        // User is locked out until far in the future
        var lockoutEnd = new DateTime(2025, 6, 1, 13, 0, 0, DateTimeKind.Utc);
        var user = MakeUser(failedCount: 5, lockoutEnd: lockoutEnd);
        _repo.FindUserByEmailWithRolesAsync("testuser@example.com", Arg.Any<CancellationToken>()).Returns(user);

        // Even with the CORRECT password, login must be rejected
        var result = await _sut.LoginAsync(new LoginRequest { Email = "testuser@example.com", Password = Password }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        // IncrementFailedLoginAsync should NOT have been called - we short-circuited before BCrypt
        await _repo.DidNotReceive().IncrementFailedLoginAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_SuccessAfterFails_ClearsLockout()
    {
        // User has 3 prior failures but is NOT locked out
        var user = MakeUser(failedCount: 3);
        _repo.FindUserByEmailWithRolesAsync("testuser@example.com", Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.LoginAsync(new LoginRequest { Email = "testuser@example.com", Password = Password }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Token));
        // ResetFailedLoginAsync must have been called to clear the counter
        await _repo.Received(1).ResetFailedLoginAsync(user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_ExponentialBackoff_SecondLockoutIsDoubled()
    {
        var user = MakeUser(failedCount: 9);
        _repo.FindUserByEmailWithRolesAsync("testuser@example.com", Arg.Any<CancellationToken>()).Returns(user);
        _repo.IncrementFailedLoginAsync(user.Id, Arg.Any<CancellationToken>()).Returns(10);

        await _sut.LoginAsync(new LoginRequest { Email = "testuser@example.com", Password = "wrong" }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).LockoutUserAsync(
            user.Id,
            Arg.Is<DateTime>(d => d > new DateTime(2025, 6, 1, 12, 29, 0, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_LockoutExpired_AllowsLogin()
    {
        var lockoutEnd = new DateTime(2025, 6, 1, 11, 0, 0, DateTimeKind.Utc);
        var user = MakeUser(failedCount: 5, lockoutEnd: lockoutEnd);
        _repo.FindUserByEmailWithRolesAsync("testuser@example.com", Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.LoginAsync(new LoginRequest { Email = "testuser@example.com", Password = Password }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task UnlockUserAsync_ClearsLockout()
    {
        _repo.FindUserByIdWithRolesAsync(1, Arg.Any<CancellationToken>()).Returns(MakeUser(5));

        var result = await _sut.UnlockUserAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repo.Received(1).ClearLockoutAsync(1, Arg.Any<CancellationToken>());
    }
}
