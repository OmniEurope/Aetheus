// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Servers;

public sealed class MetricsCleanupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<MetricsCleanupService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : PeriodicBackgroundService(TimeSpan.FromHours(6), leaderLease, "aetheus:metrics-cleanup")
{
    protected override Task ExecuteIterationAsync(CancellationToken ct) => CleanupExpiredMetricsAsync(ct);

    protected override void LogIterationError(Exception exception) =>
        logger.LogError(exception, "Error during metrics cleanup");

    internal async Task CleanupExpiredMetricsAsync(CancellationToken ct)
    {
        var retentionDays = Math.Clamp(configuration.GetValue("Metrics:RetentionDays", 30), 1, 3650);
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IServerHeartbeatRepository>();
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-retentionDays);
        var deleted = await repo.DeleteMetricsOlderThanAsync(cutoff, ct).ConfigureAwait(false);

        if (deleted > 0)
            logger.LogInformation("Deleted {Count} metrics older than {Days} days", deleted, retentionDays);
    }
}
