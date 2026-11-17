// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// Runs <see cref="AnalysisDecisionExpiry"/> every maintenance interval, on the leader instance only (recette R-24):
/// an accepted risk comes back as open without waiting for a new report of its project.
/// </summary>
public sealed class AnalysisDecisionExpiryService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<AnalysisDecisionExpiryService> logger,
    IPostgresLeaderLease leaderLease) : LeaderBackgroundService(leaderLease, "aetheus:analysis-decision-expiry")
{
    protected override async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        await RunCycleAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(BackendRuntimeDefaults.MaintenanceInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RunCycleAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try { await ReopenAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogError(exception, "Analysis decision expiry cycle failed"); }
    }

    internal async Task ReopenAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AnalysisDecisionExpiry>().ReopenExpiredAsync(ct).ConfigureAwait(false);
    }
}
