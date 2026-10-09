// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;

namespace Aetheus.Back.Components.Projects;

/// <summary>
/// PLAN-013 lot 5: enforces the project storage budgets once an hour, on the leader backend only. The
/// first sweep waits one interval after startup, so a restart never starts with the row counts.
/// </summary>
public sealed class ProjectStorageBudgetWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ProjectStorageBudgetWorker> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    internal const string LeaseName = "aetheus:project-storage-budget";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync(LeaseName, RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BackendRuntimeDefaults.MaintenanceInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await SweepAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>One sweep; a failure is logged and the next sweep runs.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IProjectStorageBudgetService>()
                .EnforceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception exception)
        {
            // Mandatory: a failed sweep must not stop the next one.
            logger.LogError(exception, "Project storage budget sweep failed");
        }
    }
}
