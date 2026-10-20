// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Cronos;

namespace Aetheus.Back.Components.Rkhunter;

public class RkhunterSchedulerService(IServiceScopeFactory scopeFactory, ILogger<RkhunterSchedulerService> logger, TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    // Only the live colour runs it (decision of 2026-10-02, PostgresLeaderLease).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:rkhunter-scheduler", RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BackendRuntimeDefaults.SchedulerCheckInterval, timeProvider);

        do
        {
            await CheckScheduledScansAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task CheckScheduledScansAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRkhunterRepository>();
        var taskService = scope.ServiceProvider.GetRequiredService<ITaskService>();

        List<RkhunterState> states;
        try
        {
            states = await repo.GetScheduledStatesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch RKHunter scheduled states");
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var state in states)
        {
            try
            {
                var cron = CronExpression.Parse(state.ScanScheduleCron!);
                var next = cron.GetNextOccurrence(now.AddMinutes(-1), inclusive: true);
                if (next is null || next.Value > now || next.Value <= now.AddMinutes(-1))
                    continue;

                if (state.LastScheduledScanAt.HasValue && (now - state.LastScheduledScanAt.Value).TotalMinutes < 2)
                    continue;

                // Items #10.2/#10.3: route the scheduled scan through the same typed-op +
                // sudo pipeline as on-demand scans. No more shell, no allow-list drift risk.
                var scanTask = ServerTaskFactory.Operation(state.ServerId,
                    "RKHunter - scheduled scan", OperationKind.RkhunterScan, target: "-", timeoutSeconds: 300);
                await repo.AddTaskAsync(scanTask, ct).ConfigureAwait(false);
                await taskService.NotifyTaskQueuedAsync(scanTask, ct: ct).ConfigureAwait(false);

                await repo.UpdateLastScheduledScanAsync(state.ServerId, ct).ConfigureAwait(false);
                logger.LogInformation("Scheduled RKHunter scan triggered for server {ServerId}", state.ServerId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process RKHunter schedule for server {ServerId}", state.ServerId);
            }
        }
    }
}
