// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// Checks every minute whether the daily recap is due. Only the leader instance (the live
/// blue-green colour) runs it, and the day it sends is claimed in the database first, so the recap
/// leaves once per day; a backend that was down at the send time sends it when it comes back the
/// same day.
/// </summary>
public sealed class DailyRecapSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<DailyRecapSchedulerService> logger,
    IPostgresLeaderLease? leaderLease = null)
    : PeriodicBackgroundService(CheckInterval, leaderLease, "aetheus:daily-recap")
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override Task ExecuteIterationAsync(CancellationToken ct) => CheckAsync(ct);

    protected override void LogIterationError(Exception exception) =>
        logger.LogError(exception, "Error during the daily recap check");

    internal async Task CheckAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DailyRecapService>();
        await service.RunScheduledAsync(ct).ConfigureAwait(false);
    }
}
