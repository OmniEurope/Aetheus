// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// Checks every minute whether the nightly CVE mail is due (<see cref="CveWatchDigestSchedule"/>). Only the leader
/// instance (the live blue-green colour) runs it.
/// </summary>
public sealed class CveWatchDigestSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<CveWatchDigestSchedulerService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease leaderLease)
    : PeriodicBackgroundService(CheckInterval, leaderLease, "aetheus:cve-watch-digest", timeProvider)
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override Task ExecuteIterationAsync(CancellationToken ct) => CheckAsync(ct);

    protected override void LogIterationError(Exception exception) =>
        logger.LogError(exception, "Error during the nightly CVE mail check");

    internal async Task CheckAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var schedule = scope.ServiceProvider.GetRequiredService<CveWatchDigestSchedule>();
        await schedule.RunScheduledAsync(ct).ConfigureAwait(false);
    }
}
