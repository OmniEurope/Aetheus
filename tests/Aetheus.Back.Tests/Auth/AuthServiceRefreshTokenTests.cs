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

public class AuthServiceRefreshTokenTests
{
    private readonly IAuthRepository _repo = Substitute.For<IAuthRepository>();
    private readonly Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher _domainEvents =
        Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>();
    private readonly AuthService _sut;

    public AuthServiceRefreshTokenTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:JwtKey"] = "aetheus-dev-key-minimum-32-bytes!!"
            })
            .Build();

        _sut = new AuthService(
            _repo, config, Substitute.For<IAuditService>(),
            new JwtOptions { SigningKey = "aetheus-dev-key-minimum-32-bytes!!" },
            Substitute.For<IMemoryCache>(), Substitute.For<IOrganizationService>(),
            Substitute.For<ITotpService>(), TimeProvider.System,
            Substitute.For<IHttpContextAccessor>(), Substitute.For<Aetheus.Back.Services.IAdminChangeNotifier>(), _domainEvents, Substitute.For<ILogger<AuthService>>());

        // The revoke is a compare-and-set (R-072): by default this request is the one that wins it.
        _repo.RevokeRefreshTokenAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static User ActiveUser(int id = 1) =>
        new() { Id = id, Username = "bob", IsActive = true, UserRoles = [] };

    private void Stored(RefreshToken token) =>
        _repo.FindRefreshTokenByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(token);

    [Fact]
    public async Task RefreshToken_NotFound_ReturnsNull()
    {
        _repo.FindRefreshTokenByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((RefreshToken?)null);

        Assert.Null(await _sut.RefreshTokenAsync("plaintext", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshToken_Valid_RotatesAndReturnsNewRefreshToken()
    {
        Stored(new RefreshToken
        {
            Id = 10,
            UserId = 1,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            User = ActiveUser()
        });

        var result = await _sut.RefreshTokenAsync("plaintext", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.False(string.IsNullOrEmpty(result.RefreshToken));
        Assert.NotEqual("plaintext", result.RefreshToken);
        await _repo.Received(1).AddRefreshTokenAsync(Arg.Any<RefreshToken>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).RevokeRefreshTokenAsync(10, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshToken_Expired_ReturnsNull()
    {
        Stored(new RefreshToken
        {
            Id = 11,
            UserId = 1,
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
            User = ActiveUser()
        });

        Assert.Null(await _sut.RefreshTokenAsync("plaintext", ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddRefreshTokenAsync(Arg.Any<RefreshToken>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshToken_InactiveUser_ReturnsNull()
    {
        Stored(new RefreshToken
        {
            Id = 12,
            UserId = 1,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            User = new User { Id = 1, Username = "bob", IsActive = false, UserRoles = [] }
        });

        Assert.Null(await _sut.RefreshTokenAsync("plaintext", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshToken_RevokedOutsideGrace_RevokesAllAndReturnsNull()
    {
        Stored(new RefreshToken
        {
            Id = 13,
            UserId = 5,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            RevokedAt = DateTime.UtcNow.AddHours(-1), // well outside the grace window
            User = ActiveUser(5)
        });

        var result = await _sut.RefreshTokenAsync("plaintext", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repo.Received(1).RevokeAllUserRefreshTokensAsync(5, Arg.Any<CancellationToken>());
        // R1-37: the revoked chain also ends what the modules hold in memory for that user.
        await _domainEvents.Received(1).DispatchAsync(
            new Aetheus.Back.Components.Users.Events.UserSessionEndedEvent(5, Aetheus.Back.Components.Users.Events.UserSessionEndReasons.RefreshReplay),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshToken_RevokedWithinGraceWithReplacement_ReissuesAccessTokenOnly()
    {
        Stored(new RefreshToken
        {
            Id = 14,
            UserId = 1,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            RevokedAt = DateTime.UtcNow, // just revoked → inside grace
            ReplacedById = 99,
            User = ActiveUser()
        });

        var result = await _sut.RefreshTokenAsync("plaintext", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Token));
        // Grace re-issue does NOT mint a new refresh token.
        await _repo.DidNotReceive().AddRefreshTokenAsync(Arg.Any<RefreshToken>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// R-072: two requests read the same active token, and the other one revoked it first. The loser
    /// must not fail (it used to end in a 500) nor fork the chain: it retires the replacement it minted
    /// and is answered like a request arriving inside the grace window, an access token only.
    /// </summary>
    [Fact]
    public async Task RefreshToken_LostConcurrentRotation_ReissuesAccessTokenOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        Stored(new RefreshToken { Id = 30, UserId = 1, ExpiresAt = DateTime.UtcNow.AddDays(1), User = ActiveUser() });
        _repo.RevokeRefreshTokenAsync(30, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        _repo.FindRefreshTokenStateAsync(30, Arg.Any<CancellationToken>())
            .Returns(new RefreshToken { Id = 30, UserId = 1, RevokedAt = DateTime.UtcNow, ReplacedById = 31 });

        var outcome = await _sut.RefreshTokenWithReasonAsync("plaintext", ct);

        Assert.Null(outcome.RejectionCode);
        Assert.False(string.IsNullOrEmpty(outcome.Response!.Token));
        Assert.Null(outcome.Response.RefreshToken);
        // The replacement minted by the loser (Id 0 under the mock) is revoked, never handed out.
        await _repo.Received(1).RevokeRefreshTokenAsync(0, null, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().RevokeAllUserRefreshTokensAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _domainEvents.DidNotReceive().DispatchAsync(
            Arg.Any<Aetheus.Back.Components.Users.Events.UserSessionEndedEvent>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().DeleteExpiredRefreshTokensAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// R-072: when what beat this request was a revocation without replacement (a logout), the reuse
    /// keeps the existing replay rule: the chain is revoked and the refusal names the replay.
    /// </summary>
    [Fact]
    public async Task RefreshToken_LostToARevocationWithoutReplacement_IsTreatedAsReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        Stored(new RefreshToken { Id = 32, UserId = 7, ExpiresAt = DateTime.UtcNow.AddDays(1), User = ActiveUser(7) });
        _repo.RevokeRefreshTokenAsync(32, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        _repo.FindRefreshTokenStateAsync(32, Arg.Any<CancellationToken>())
            .Returns(new RefreshToken { Id = 32, UserId = 7, RevokedAt = DateTime.UtcNow });

        var outcome = await _sut.RefreshTokenWithReasonAsync("plaintext", ct);

        Assert.Null(outcome.Response);
        Assert.Equal(RefreshRejectionCodes.Replay, outcome.RejectionCode);
        await _repo.Received(1).RevokeAllUserRefreshTokensAsync(7, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// PLAN-005 lot 9 / D48: each of the refusals has its own code, so a session that ended can say
    /// which one ended it; a success carries none.
    /// </summary>
    [Fact]
    public async Task RefreshTokenWithReason_NamesEachRefusal()
    {
        var ct = TestContext.Current.CancellationToken;

        _repo.FindRefreshTokenByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((RefreshToken?)null);
        Assert.Equal(RefreshRejectionCodes.UnknownToken, (await _sut.RefreshTokenWithReasonAsync("x", ct)).RejectionCode);

        Stored(new RefreshToken { Id = 20, UserId = 5, ExpiresAt = DateTime.UtcNow.AddDays(1), RevokedAt = DateTime.UtcNow.AddHours(-1), User = ActiveUser(5) });
        Assert.Equal(RefreshRejectionCodes.Replay, (await _sut.RefreshTokenWithReasonAsync("x", ct)).RejectionCode);

        Stored(new RefreshToken { Id = 21, UserId = 1, ExpiresAt = DateTime.UtcNow.AddDays(-1), User = ActiveUser() });
        Assert.Equal(RefreshRejectionCodes.Expired, (await _sut.RefreshTokenWithReasonAsync("x", ct)).RejectionCode);

        Stored(new RefreshToken { Id = 22, UserId = 1, ExpiresAt = DateTime.UtcNow.AddDays(1), User = new User { Id = 1, Username = "bob", IsActive = false, UserRoles = [] } });
        Assert.Equal(RefreshRejectionCodes.UserInactive, (await _sut.RefreshTokenWithReasonAsync("x", ct)).RejectionCode);

        Stored(new RefreshToken { Id = 23, UserId = 1, ExpiresAt = DateTime.UtcNow.AddDays(1), User = ActiveUser() });
        var success = await _sut.RefreshTokenWithReasonAsync("x", ct);
        Assert.NotNull(success.Response);
        Assert.Null(success.RejectionCode);
    }

    /// <summary>Recette R1-37: an explicit logout announces the end of the user's session in-process.</summary>
    [Fact]
    public async Task Logout_AnnouncesTheEndOfTheUsersSession()
    {
        await _sut.LogoutAsync(42, TestContext.Current.CancellationToken);

        await _domainEvents.Received(1).DispatchAsync(
            new Aetheus.Back.Components.Users.Events.UserSessionEndedEvent(42, Aetheus.Back.Components.Users.Events.UserSessionEndReasons.Logout),
            Arg.Any<CancellationToken>());
    }
}
