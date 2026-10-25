// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Alerts;

/// <summary>Hourly, on the leader, deletes the alert occurrences older than <see cref="RetentionDays"/>
/// so the table does not grow without bound.</summary>
public sealed class AlertOccurrenceRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<AlertOccurrenceRetentionService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    /// <summary>Alert occurrences are kept 365 days, measured on <c>TriggeredAt</c> (decision of
    /// 2026-10-23). A notification mail whose alert was purged opens the existing "alert not found"
    /// state.</summary>
    internal const int RetentionDays = 365;

    internal const string LeaseName = "aetheus:alert-occurrence-retention";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync(LeaseName, RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BackendRuntimeDefaults.MaintenanceInterval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during alert occurrence retention sweep");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAlertOccurrenceRepository>();
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-RetentionDays);
        var deleted = await repository.DeleteTriggeredBeforeAsync(cutoff, ct).ConfigureAwait(false);
        if (deleted > 0)
            logger.LogInformation(
                "Alert occurrence retention: deleted {Count} occurrences triggered before {Cutoff:O}", deleted, cutoff);
        return deleted;
    }
}
