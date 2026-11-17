// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Tests.Servers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

/// <summary>R-53: one HKDF-derived key per purpose instead of the JWT signing key, with a fallback that
/// still accepts the tokens stored before the change, proved through the real repositories.</summary>
public sealed class TokenHashKeysTests : IDisposable
{
    private const string SigningKey = "aetheus-dev-key-minimum-32-bytes!!";
    private const int UserId = 42;
    private const int ServerId = 7;
    private static readonly TokenHashKeys Keys = new(new JwtOptions { SigningKey = SigningKey });
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
    private readonly AppDbContext _db;
    private readonly AuthService _auth;

    public TokenHashKeysTests()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            _clock);
        _db.Users.Add(new User { Id = UserId, Username = "carol", IsActive = true, SecurityStamp = "s", UserRoles = [] });
        _db.Servers.Add(new Server { Id = ServerId, Name = "web-1", Hostname = "web-1", OrganizationId = 1 });
        _db.SaveChanges();
        _auth = new AuthService(
            new AuthRepository(_db, _clock), new ConfigurationBuilder().Build(), Substitute.For<IAuditService>(),
            new JwtOptions { SigningKey = SigningKey }, Keys,
            Substitute.For<IMemoryCache>(), Substitute.For<IOrganizationService>(), Substitute.For<ITotpService>(), _clock,
            Substitute.For<IHttpContextAccessor>(), Substitute.For<Aetheus.Back.Services.IAdminChangeNotifier>(),
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(), Substitute.For<ILogger<AuthService>>());
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string LegacyHash(string token) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey), Encoding.UTF8.GetBytes(token)));

    [Fact]
    public void EachPurpose_HasItsOwnKey_AndNoneIsTheSigningKey()
    {
        var signingKey = Encoding.UTF8.GetBytes(SigningKey);
        var keys = Enum.GetValues<TokenHashPurpose>().Select(Keys.KeyFor).ToList();

        Assert.Equal(Enum.GetValues<TokenHashPurpose>().Length, keys.Select(Convert.ToHexString).Distinct().Count());
        Assert.All(keys, key =>
        {
            Assert.Equal(32, key.Length);
            Assert.False(key.AsSpan().SequenceEqual(signingKey));
        });
        // The same token hashes differently for each purpose, and never as the legacy scheme did.
        var hashes = Enum.GetValues<TokenHashPurpose>().Select(purpose => Keys.Hash(purpose, "same-token")).ToList();
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
        Assert.DoesNotContain(LegacyHash("same-token"), hashes);
    }

    [Fact]
    public void TheKeysAreDerivedFromTheSigningKey_SoTheyAreStableAndFollowItsRotation()
    {
        var again = new TokenHashKeys(new JwtOptions { SigningKey = SigningKey });
        var rotated = new TokenHashKeys(new JwtOptions { SigningKey = "another-key-of-at-least-32-bytes!!!" });

        Assert.All(Enum.GetValues<TokenHashPurpose>(), purpose =>
        {
            Assert.Equal(Keys.KeyFor(purpose), again.KeyFor(purpose));
            Assert.NotEqual(Keys.KeyFor(purpose), rotated.KeyFor(purpose));
        });
        Assert.NotEqual(Keys.AnalyticsVisitorId("1"), rotated.AnalyticsVisitorId("1"));
    }

    [Fact]
    public void Candidates_AreTheNewHashFirst_ThenTheLegacyHash()
    {
        var candidates = Keys.HashCandidates(TokenHashPurpose.RefreshToken, "token");

        Assert.Equal([Keys.Hash(TokenHashPurpose.RefreshToken, "token"), LegacyHash("token")], candidates);
    }

    [Fact]
    public async Task ARefreshTokenStoredUnderTheLegacyHash_IsStillAccepted_AndItsReplacementCarriesTheNewHash()
    {
        const string plaintext = "refresh-issued-before-r53";
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = UserId,
            TokenHash = LegacyHash(plaintext),
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            ExpiresAt = _clock.GetUtcNow().UtcDateTime.AddDays(10)
        });
        await _db.SaveChangesAsync(Ct);

        var outcome = await _auth.RefreshTokenWithReasonAsync(plaintext, Ct);

        Assert.Null(outcome.RejectionCode);
        var replacement = outcome.Response!.RefreshToken!;
        var rows = await _db.RefreshTokens.AsNoTracking().OrderBy(t => t.Id).ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].RevokedAt);
        Assert.Equal(Keys.Hash(TokenHashPurpose.RefreshToken, replacement), rows[1].TokenHash);
        Assert.NotEqual(LegacyHash(replacement), rows[1].TokenHash);
        // The replacement keeps working through the new hash.
        Assert.Null((await _auth.RefreshTokenWithReasonAsync(replacement, Ct)).RejectionCode);
    }

    [Fact]
    public async Task ANewRefreshToken_IsStoredUnderTheNewHash()
    {
        var issued = (await _auth.RenewTokenAsync(UserId, Ct))!.RefreshToken!;

        var row = await _db.RefreshTokens.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Keys.Hash(TokenHashPurpose.RefreshToken, issued), row.TokenHash);
    }

    [Fact]
    public async Task AnAgentTokenStoredUnderTheLegacyHash_IsStillAccepted_ButNotAHashOfAnotherPurpose()
    {
        _db.ServerTokens.Add(new ServerToken { ServerId = ServerId, TokenHash = LegacyHash("agent-legacy"), ExpiresAt = _clock.GetUtcNow().UtcDateTime.AddDays(300) });
        _db.ServerTokens.Add(new ServerToken
        {
            ServerId = ServerId,
            TokenHash = Keys.Hash(TokenHashPurpose.RefreshToken, "agent-wrong-purpose"),
            ExpiresAt = _clock.GetUtcNow().UtcDateTime.AddDays(300)
        });
        await _db.SaveChangesAsync(Ct);

        Assert.Equal(ServerId, await _auth.ValidateServerTokenAsync("agent-legacy", Ct));
        Assert.Null(await _auth.ValidateServerTokenAsync("agent-wrong-purpose", Ct));
    }

    [Fact]
    public async Task AnEnrollmentTokenStoredUnderTheLegacyHash_StillEnrolls_AndTheAgentTokenCarriesTheNewHash()
    {
        var scenario = new RetiredServerScenario();
        await scenario.SeedAsync();
        var token = await scenario.AddRegistrationTokenAsync(legacyHash: true);

        var response = await scenario.EnrollAsync(RetiredServerScenario.Request(token, "fresh-host", machineIdHash: null));

        Assert.NotNull(response);
        await using var db = scenario.NewContext();
        var auth = new AuthRepository(db, scenario.Clock);
        Assert.Equal(response.ServerId,
            await auth.ValidateServerTokenHashAsync([RetiredServerScenario.Keys.Hash(TokenHashPurpose.ServerToken, response.BearerToken)], Ct));
        Assert.True(await db.RegistrationTokens.AsNoTracking().AllAsync(t => t.IsUsed, Ct));
    }
}
