// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OtpNet;

namespace Aetheus.Back.Tests.Auth;

/// <summary>R-50: disabling 2FA through the real repository: the other sessions end, the caller gets a new one.</summary>
public sealed class AuthServiceTotpDisableTests : IDisposable
{
    private const string Password = "Correct-Password-1!";
    private readonly AppDbContext _db;
    private readonly AuthService _sut;
    private readonly byte[] _secret = KeyGeneration.GenerateRandomKey(20);

    public AuthServiceTotpDisableTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var repo = new AuthRepository(_db, TimeProvider.System);
        var encryption = Substitute.For<IEncryptionService>();
        encryption.DecryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>().Replace("enc:", "", StringComparison.Ordinal));
        var jwt = new JwtOptions { SigningKey = "aetheus-dev-key-minimum-32-bytes!!" };
        var audit = Substitute.For<IAuditService>();
        var totp = new TotpService(repo, encryption, audit, jwt, TimeProvider.System);
        _sut = new AuthService(
            repo, new ConfigurationBuilder().Build(), audit, jwt, new TokenHashKeys(jwt),
            new MemoryCache(new MemoryCacheOptions()), Substitute.For<IOrganizationService>(),
            totp, TimeProvider.System,
            Substitute.For<IHttpContextAccessor>(), Substitute.For<IAdminChangeNotifier>(),
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(), Substitute.For<ILogger<AuthService>>());

        var now = DateTime.UtcNow;
        _db.Users.Add(new User
        {
            Id = 1,
            Username = "bob",
            Email = "bob@example.com",
            IsActive = true,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            TotpEnabled = true,
            TotpSecret = $"enc:{Base32Encoding.ToString(_secret)}",
            SecurityStamp = "stamp-before",
            UserRoles = []
        });
        _db.RefreshTokens.Add(new RefreshToken { Id = 10, UserId = 1, TokenHash = "other-session", CreatedAt = now, ExpiresAt = now.AddDays(1) });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private string CurrentCode() => new Totp(_secret, step: 30, totpSize: 6).ComputeTotp(DateTime.UtcNow);

    [Fact]
    public async Task ValidCode_EndsOtherSessionsAndReturnsANewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        // The old stamp is valid, and cached, before the change.
        Assert.True(await _sut.IsSecurityStampValidAsync(1, "stamp-before", ct));

        var session = await _sut.DisableTotpAsync(1, new TotpDisableRequest { Password = Password, Code = CurrentCode() }, ct);

        Assert.NotNull(session);
        Assert.False(string.IsNullOrEmpty(session.Token));
        Assert.False(string.IsNullOrEmpty(session.RefreshToken));
        var user = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == 1, ct);
        Assert.False(user.TotpEnabled);
        Assert.NotEqual("stamp-before", user.SecurityStamp);
        // A token of another session (previous stamp) is refused at once, the cache entry was dropped.
        Assert.False(await _sut.IsSecurityStampValidAsync(1, "stamp-before", ct));
        Assert.True(await _sut.IsSecurityStampValidAsync(1, user.SecurityStamp, ct));
        var other = await _db.RefreshTokens.AsNoTracking().SingleAsync(r => r.Id == 10, ct);
        Assert.NotNull(other.RevokedAt);
        // The caller's new refresh token is the only active one.
        Assert.Equal(1, await _db.RefreshTokens.CountAsync(r => r.UserId == 1 && r.RevokedAt == null, ct));
    }

    [Fact]
    public async Task NoCode_RefusedAndNothingEnds()
    {
        var ct = TestContext.Current.CancellationToken;

        var session = await _sut.DisableTotpAsync(1, new TotpDisableRequest { Password = Password, Code = "" }, ct);

        Assert.Null(session);
        var user = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == 1, ct);
        Assert.True(user.TotpEnabled);
        Assert.Equal("stamp-before", user.SecurityStamp);
        Assert.Equal(1, user.FailedLoginCount);
        Assert.Null((await _db.RefreshTokens.AsNoTracking().SingleAsync(r => r.Id == 10, ct)).RevokedAt);
    }

    [Fact]
    public async Task FiveFailures_LockTheAccount_ThenTheRightCodeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var attempt = 0; attempt < LoginLockoutPolicyThreshold; attempt++)
            Assert.Null(await _sut.DisableTotpAsync(1, new TotpDisableRequest { Password = "wrong", Code = CurrentCode() }, ct));

        var locked = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == 1, ct);
        Assert.NotNull(locked.LockoutEndUtc);
        Assert.True(locked.LockoutEndUtc > DateTime.UtcNow);

        Assert.Null(await _sut.DisableTotpAsync(1, new TotpDisableRequest { Password = Password, Code = CurrentCode() }, ct));
        Assert.True((await _db.Users.AsNoTracking().SingleAsync(u => u.Id == 1, ct)).TotpEnabled);
    }

    [Fact]
    public void DisableEndpoint_CarriesTheLoginRateLimit()
    {
        // The rate limiter is switched off in the test hosts, so the policy is proved on the action itself.
        var action = typeof(AuthController).GetMethod(nameof(AuthController.DisableTotp))!;
        var limit = action.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(limit);
        Assert.Equal("login", limit.PolicyName);
    }

    private const int LoginLockoutPolicyThreshold = 5;
}
