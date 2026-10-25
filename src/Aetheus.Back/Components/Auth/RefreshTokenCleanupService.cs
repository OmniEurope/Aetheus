// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Auth;

public sealed class RefreshTokenCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<RefreshTokenCleanupService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : PeriodicBackgroundService(RefreshTokenRetention.SweepInterval, leaderLease, "aetheus:refresh-token-cleanup")
{
    protected override Task ExecuteIterationAsync(CancellationToken ct) => CleanupExpiredTokensAsync(ct);

    protected override void LogIterationError(Exception exception) =>
        logger.LogError(exception, "Error during refresh token cleanup");

    internal async Task CleanupExpiredTokensAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAuthRepository>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var deleted = await repo.DeleteAllExpiredRefreshTokensAsync(now, ct).ConfigureAwait(false);

        if (deleted > 0)
            logger.LogInformation("Purged {Count} expired/revoked refresh tokens", deleted);
    }
}
