// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// End-to-end exercise of the refresh-token rotation flow (F-012) through the real HTTP
/// pipeline backed by PostgreSQL.
/// <para>
/// The refresh token lifecycle is: login → receive token pair → use refresh token →
/// receive new pair → old refresh token is revoked (but honored within a 5-minute grace
/// window for in-flight concurrent requests). After the grace window, the old token is
/// fully rejected. These flows depend on real relational state (the <c>RefreshTokens</c>
/// table, hash lookups, chain revocation) that InMemory cannot model.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RefreshTokenIntegrationTests(PostgresFixture fixture)
{
    private const string AdminPassword = "Integr@tion-Test-Admin-Pwd-2026";

    [Fact]
    public async Task RefreshToken_ReturnsNewTokenPair_AndOldTokenStillWorksInGrace()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        // Step 1: Login
        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "admin",
            Password = AdminPassword
        }, cancellationToken: TestContext.Current.CancellationToken);
        loginResp.EnsureSuccessStatusCode();
        var firstLogin = await loginResp.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(firstLogin);
        Assert.False(string.IsNullOrEmpty(firstLogin!.Token));
        Assert.False(string.IsNullOrEmpty(firstLogin.RefreshToken));
        Assert.False(firstLogin.TotpRequired);

        // Step 2: Use the refresh token to get a new pair
        var refreshResp = await client.PostAsJsonAsync("/api/auth/token/refresh", new RefreshTokenRequest
        {
            RefreshToken = firstLogin.RefreshToken!
        }, cancellationToken: TestContext.Current.CancellationToken);
        refreshResp.EnsureSuccessStatusCode();
        var secondLogin = await refreshResp.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(secondLogin);
        Assert.False(string.IsNullOrEmpty(secondLogin!.Token));
        Assert.False(string.IsNullOrEmpty(secondLogin.RefreshToken));

        // The new refresh token should differ from the old one (rotation happened)
        Assert.NotEqual(firstLogin.RefreshToken, secondLogin.RefreshToken);

        // Step 3: The old refresh token should still work within the 5-minute grace window
        // (simulating a concurrent in-flight request)
        var graceResp = await client.PostAsJsonAsync("/api/auth/token/refresh", new RefreshTokenRequest
        {
            RefreshToken = firstLogin.RefreshToken!
        }, cancellationToken: TestContext.Current.CancellationToken);
        // Within the grace window, this should succeed (returns 200, not 401)
        Assert.Equal(HttpStatusCode.OK, graceResp.StatusCode);

        // Step 4: The new refresh token is still valid
        var thirdResp = await client.PostAsJsonAsync("/api/auth/token/refresh", new RefreshTokenRequest
        {
            RefreshToken = secondLogin.RefreshToken!
        }, cancellationToken: TestContext.Current.CancellationToken);
        thirdResp.EnsureSuccessStatusCode();
        var thirdLogin = await thirdResp.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrEmpty(thirdLogin!.Token));
    }

    /// <summary>Concurrent refreshes must succeed while only one rotates the shared
    /// token.</summary>
    [Fact]
    public async Task ParallelRefreshesOfOneToken_NeverFail_AndRotateExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Username = "admin", Password = AdminPassword }, cancellationToken: ct))
            .Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, cancellationToken: ct);

        int userId;
        using (var scope = factory.Services.CreateScope())
        {
            // Dead rows for the opportunistic prune, as a user who has been signed in for a while has.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            userId = (await db.Users.SingleAsync(u => u.Username == "admin", ct)).Id;
            for (var i = 0; i < 5; i++)
                db.RefreshTokens.Add(new Aetheus.Back.Data.Entities.RefreshToken
                {
                    UserId = userId,
                    TokenHash = Guid.NewGuid().ToString("N"),
                    CreatedAt = DateTime.UtcNow.AddDays(-40),
                    ExpiresAt = DateTime.UtcNow.AddDays(-10)
                });
            await db.SaveChangesAsync(ct);
        }

        var firedAt = DateTime.UtcNow.AddSeconds(-1);
        using var start = new SemaphoreSlim(0);
        var requests = Enumerable.Range(0, 8).Select(async _ =>
        {
            await start.WaitAsync(ct);
            return await client.PostAsJsonAsync("/api/auth/token/refresh",
                new RefreshTokenRequest { RefreshToken = login!.RefreshToken! }, cancellationToken: ct);
        }).ToList();
        start.Release(requests.Count);
        var responses = await Task.WhenAll(requests);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r =>
            r.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, cancellationToken: ct)));
        Assert.All(bodies, b => Assert.False(string.IsNullOrEmpty(b!.Token)));
        var rotated = Assert.Single(bodies, b => b!.RefreshToken is not null);

        // The collection runs serially, so every token minted since the burst belongs to it: the
        // one rotated token is the only live one, the losers' replacements were retired. No fork of
        // the chain.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var minted = await db.RefreshTokens.AsNoTracking()
                .Where(t => t.UserId == userId && t.CreatedAt >= firedAt)
                .ToListAsync(ct);
            Assert.Single(minted, t => t.RevokedAt == null);
        }

        var next = await client.PostAsJsonAsync("/api/auth/token/refresh",
            new RefreshTokenRequest { RefreshToken = rotated!.RefreshToken! }, cancellationToken: ct);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task InvalidRefreshToken_Returns401()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var refreshResp = await client.PostAsJsonAsync("/api/auth/token/refresh", new RefreshTokenRequest
        {
            RefreshToken = "completely-invalid-token-that-does-not-exist"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, refreshResp.StatusCode);
        // The serialized unauthorized response identifies its reason.
        var error = await refreshResp.Content.ReadFromJsonAsync<ApiError>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Aetheus.Shared.Components.Auth.RefreshRejectionCodes.UnknownToken, error!.Code);
    }

    /// <summary>Anonymous session-end reports accept only a known reason and a plain correlation
    /// identifier.</summary>
    [Fact]
    public async Task SessionEnded_AcceptsAKnownReasonAnonymously_AndRefusesAnythingElse()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var known = await client.PostAsJsonAsync("/api/auth/session-ended", new SessionEndedReport
        {
            Reason = Aetheus.Shared.Components.Auth.RefreshRejectionCodes.Replay,
            CorrelationId = "0f3c9a7e2b"
        }, cancellationToken: ct);
        Assert.Equal(HttpStatusCode.NoContent, known.StatusCode);

        var unknown = await client.PostAsJsonAsync("/api/auth/session-ended", new SessionEndedReport
        {
            Reason = "anything goes",
            CorrelationId = "0f3c9a7e2b"
        }, cancellationToken: ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var injected = await client.PostAsJsonAsync("/api/auth/session-ended", new SessionEndedReport
        {
            Reason = Aetheus.Shared.Components.Auth.RefreshRejectionCodes.Replay,
            CorrelationId = "a\nFAKE LOG LINE"
        }, cancellationToken: ct);
        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
    }
}
