// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

public sealed class PipelineRetentionService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<PipelineRetentionService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (leaderLease is null)
            await RunLeaderLoopAsync(stoppingToken).ConfigureAwait(false);
        else
            await leaderLease.RunAsLeaderAsync("aetheus:pipeline-retention", RunLeaderLoopAsync, stoppingToken).ConfigureAwait(false);
    }

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await PruneOldRunsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during pipeline retention cleanup");
            }
        }
    }

    internal async Task PruneOldRunsAsync(CancellationToken ct)
    {
        // Clamp configured retention to sane bounds: minimum 1 day (avoid wiping live data),
        // maximum 3650 (~10 years) so an accidental "keep forever" config doesn't stall the cleanup.
        var retentionDays = Math.Clamp(configuration.GetValue("Retention:PipelineRunDays", 90), 1, 3650);
        var logRetentionDays = Math.Clamp(configuration.GetValue("Retention:LogDays", 30), 1, 3650);

        await using var scope = scopeFactory.CreateAsyncScope();

        var runCutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-retentionDays);
        var pipelineRepo = scope.ServiceProvider.GetRequiredService<IPipelineRunRetentionRepository>();
        var deletedRuns = await pipelineRepo.DeleteRunsOlderThanAsync(runCutoff, ct).ConfigureAwait(false);

        if (deletedRuns > 0)
            logger.LogInformation("Retention: deleted {Count} pipeline runs older than {Days} days", deletedRuns, retentionDays);

        var logCutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-logRetentionDays);
        var logRepo = scope.ServiceProvider.GetRequiredService<ILogRepository>();
        var deletedLogs = await logRepo.DeleteLogsOlderThanAsync(logCutoff, ct).ConfigureAwait(false);

        if (deletedLogs > 0)
            logger.LogInformation("Retention: deleted {Count} task logs older than {Days} days", deletedLogs, logRetentionDays);

        // Tasks had NO retention at all (only TaskLogs and PipelineRuns were purged), so the
        // table - and the every-minute status sweeps over it - grew without bound.
        var taskRetentionDays = Math.Clamp(configuration.GetValue("Retention:TaskDays", 90), 1, 3650);
        var taskCutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-taskRetentionDays);
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var deletedTasks = await taskRepo
            .DeleteCompletedTasksOlderThanAsync(taskCutoff, runCutoff, ct)
            .ConfigureAwait(false);

        if (deletedTasks > 0)
            logger.LogInformation("Retention: deleted {Count} completed tasks older than {Days} days", deletedTasks, taskRetentionDays);
    }
}
