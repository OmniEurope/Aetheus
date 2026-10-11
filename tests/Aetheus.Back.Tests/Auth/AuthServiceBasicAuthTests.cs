// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
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
            IsActive = true,
            UserRoles = [],
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FailedLoginCount = failedCount,
            LockoutEndUtc = lockoutEnd
        };

    // --- ValidateBasicAuthAsync ---

    [Fact]
    public async Task ValidateBasicAuth_UnknownUser_ReturnsFalse()
    {
        _repo.FindUserWithRolesAsync("ghost", Arg.Any<CancellationToken>()).Returns((User?)null);

        Assert.False(await _sut.ValidateBasicAuthAsync("ghost", "x", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateBasicAuth_LockedOut_ReturnsFalse()
    {
        _repo.FindUserWithRolesAsync("bob", Arg.Any<CancellationToken>())
            .Returns(UserWithPassword("secret", lockoutEnd: DateTime.UtcNow.AddMinutes(10)));

        Assert.False(await _sut.ValidateBasicAuthAsync("bob", "secret", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateBasicAuth_WrongPassword_HandlesFailureAndReturnsFalse()
    {
        _repo.FindUserWithRolesAsync("bob", Arg.Any<CancellationToken>())
            .Returns(UserWithPassword("secret"));

        Assert.False(await _sut.ValidateBasicAuthAsync("bob", "wrong", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateBasicAuth_CorrectPassword_ReturnsTrue()
    {
        _repo.FindUserWithRolesAsync("bob", Arg.Any<CancellationToken>())
            .Returns(UserWithPassword("secret"));

        Assert.True(await _sut.ValidateBasicAuthAsync("bob", "secret", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateBasicAuth_CorrectPasswordAfterFailures_ResetsCounter()
    {
        _repo.FindUserWithRolesAsync("bob", Arg.Any<CancellationToken>())
            .Returns(UserWithPassword("secret", failedCount: 3));

        Assert.True(await _sut.ValidateBasicAuthAsync("bob", "secret", ct: TestContext.Current.CancellationToken));
        await _repo.Received(1).ResetFailedLoginAsync(1, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("BOB")]
    [InlineData("Bob")]
    [InlineData("  bob  ")]
    public async Task ValidateBasicAuth_UsernameCaseAndWhitespaceInsensitive_ReturnsTrue(string typed)
    {
        // The repo only answers for the canonical stored "bob"; a true result proves the service
        // normalised the typed username (case + surrounding whitespace) before the lookup.
        _repo.FindUserWithRolesAsync("bob", Arg.Any<CancellationToken>())
            .Returns(UserWithPassword("secret"));

        Assert.True(await _sut.ValidateBasicAuthAsync(typed, "secret", ct: TestContext.Current.CancellationToken));
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
