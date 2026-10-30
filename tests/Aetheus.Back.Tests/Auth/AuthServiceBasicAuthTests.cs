// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

public class AuthServiceBasicAuthTests
{
    private readonly IAuthRepository _repo = Substitute.For<IAuthRepository>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly AuthService _sut;

    public AuthServiceBasicAuthTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:JwtKey"] = "aetheus-dev-key-minimum-32-bytes!!"
        }).Build();

        _sut = new AuthService(
            _repo, config, Substitute.For<IAuditService>(),
            new JwtOptions { SigningKey = "aetheus-dev-key-minimum-32-bytes!!" },
            _cache, Substitute.For<IOrganizationService>(),
            Substitute.For<ITotpService>(), TimeProvider.System,
            Substitute.For<IHttpContextAccessor>(), Substitute.For<Aetheus.Back.Services.IAdminChangeNotifier>(), Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(), Substitute.For<ILogger<AuthService>>());
    }

    private static User UserWithPassword(string password, int failedCount = 0, DateTime? lockoutEnd = null) =>
        new()
        {
            Id = 1,
            Username = "bob",
            Email = "bob@example.com",
            IsActive = true,
            UserRoles = [],
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FailedLoginCount = failedCount,
            LockoutEndUtc = lockoutEnd
        };

    // --- AuthenticateBasicAsync (Basic username = the account e-mail, never the username) ---

    [Fact]
    public async Task AuthenticateBasic_UnknownEmail_ReturnsNull()
    {
        _repo.FindUserByEmailWithRolesAsync("ghost@example.com", Arg.Any<CancellationToken>()).Returns((User?)null);

        Assert.Null(await _sut.AuthenticateBasicAsync("ghost@example.com", "x", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuthenticateBasic_LockedOut_ReturnsNull()
    {
        _repo.FindUserByEmailWithRolesAsync("bob@example.com", Arg.Any<CancellationToken>())
            .Returns(UserWithPassword("secret", lockoutEnd: DateTime.UtcNow.AddMinutes(10)));

        Assert.Null(await _sut.AuthenticateBasicAsync("bob@example.com", "secret", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuthenticateBasic_WrongPassword_HandlesFailureAndReturnsNull()
    {
        _repo.FindUserByEmailWithRolesAsync("bob@example.com", Arg.Any<CancellationToken>())
            .Returns(UserWithPassword("secret"));

        Assert.Null(await _sut.AuthenticateBasicAsync("bob@example.com", "wrong", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuthenticateBasic_CorrectPassword_ReturnsTheUsername()
    {
        _repo.FindUserByEmailWithRolesAsync("bob@example.com", Arg.Any<CancellationToken>())
            .Returns(UserWithPassword("secret"));

        Assert.Equal("bob", await _sut.AuthenticateBasicAsync("bob@example.com", "secret", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuthenticateBasic_CorrectPasswordAfterFailures_ResetsCounter()
    {
        _repo.FindUserByEmailWithRolesAsync("bob@example.com", Arg.Any<CancellationToken>())
            .Returns(UserWithPassword("secret", failedCount: 3));

        Assert.Equal("bob", await _sut.AuthenticateBasicAsync("bob@example.com", "secret", ct: TestContext.Current.CancellationToken));
        await _repo.Received(1).ResetFailedLoginAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuthenticateBasic_TheUsernameOfAnAccount_IsNoSignIn()
    {
        // The account "bob" exists and the password is right, but the typed value is only looked up as an
        // e-mail address: the username is a display name, never a Basic credential.
        _repo.FindUserWithRolesAsync("bob", Arg.Any<CancellationToken>()).Returns(UserWithPassword("secret"));

        Assert.Null(await _sut.AuthenticateBasicAsync("bob", "secret", ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().FindUserWithRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- IsSecurityStampValidAsync ---

    [Fact]
    public async Task IsSecurityStampValid_EmptyStamp_ReturnsFalse()
        => Assert.False(await _sut.IsSecurityStampValidAsync(1, "", ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task IsSecurityStampValid_Matches_ReturnsTrue()
    {
        _repo.GetSecurityStampAsync(1, Arg.Any<CancellationToken>()).Returns("stamp-abc");

        Assert.True(await _sut.IsSecurityStampValidAsync(1, "stamp-abc", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsSecurityStampValid_Mismatch_ReturnsFalse()
    {
        _repo.GetSecurityStampAsync(1, Arg.Any<CancellationToken>()).Returns("stamp-abc");

        Assert.False(await _sut.IsSecurityStampValidAsync(1, "stamp-xyz", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsSecurityStampValid_CachesAfterFirstLookup()
    {
        _repo.GetSecurityStampAsync(1, Arg.Any<CancellationToken>()).Returns("stamp-abc");

        await _sut.IsSecurityStampValidAsync(1, "stamp-abc", ct: TestContext.Current.CancellationToken);
        await _sut.IsSecurityStampValidAsync(1, "stamp-abc", ct: TestContext.Current.CancellationToken);

        // Second call served from cache - only one DB roundtrip.
        await _repo.Received(1).GetSecurityStampAsync(1, Arg.Any<CancellationToken>());
    }
}
