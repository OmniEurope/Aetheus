// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The refresh-token sweep on real PostgreSQL: a revoked token is deleted once it is older than the
/// retention window (a set-based DELETE), a token still inside the window is kept so reuse detection
/// keeps working, and live tokens are never touched.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RefreshTokenCleanupIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task Sweep_DeletesRevokedTokensPastTheRetentionWindow_AndKeepsTheRest()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        var past = RefreshTokenRetention.RevokedRetention + TimeSpan.FromMinutes(1);
        var inside = RefreshTokenRetention.RevokedRetention - TimeSpan.FromMinutes(1);

        await using (var seed = NewContext())
        {
            var user = new User { Username = $"sweep-{Guid.NewGuid():N}", PasswordHash = "x", CreatedAt = now, UpdatedAt = now };
            seed.Users.Add(user);
            await seed.SaveChangesAsync(ct);
            seed.RefreshTokens.AddRange(
                Token(user.Id, "revoked-past", now.AddDays(30), now - past),
                Token(user.Id, "revoked-inside", now.AddDays(30), now - inside),
                Token(user.Id, "active", now.AddDays(30), null),
                Token(user.Id, "expired", now.AddMinutes(-1), null));
            await seed.SaveChangesAsync(ct);
        }

        var services = new ServiceCollection()
            .AddScoped(_ => NewContext())
            .AddScoped<IAuthRepository>(provider => new AuthRepository(provider.GetRequiredService<AppDbContext>(), TimeProvider.System))
            .BuildServiceProvider();
        var sweep = new RefreshTokenCleanupService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RefreshTokenCleanupService>.Instance,
            TimeProvider.System);

        await sweep.CleanupExpiredTokensAsync(ct);

        await using var verify = NewContext();
        var remaining = await verify.RefreshTokens.AsNoTracking().Select(token => token.TokenHash).OrderBy(hash => hash).ToListAsync(ct);
        Assert.Equal(["active", "revoked-inside"], remaining);
    }

    private static RefreshToken Token(int userId, string hash, DateTime expiresAt, DateTime? revokedAt) =>
        new() { UserId = userId, TokenHash = hash, CreatedAt = DateTime.UtcNow, ExpiresAt = expiresAt, RevokedAt = revokedAt };
}
