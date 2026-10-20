// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Cronos;

namespace Aetheus.Back.Components.AiTasks;

public sealed class AiTaskSchedulerService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<AiTaskSchedulerService> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = BackendRuntimeDefaults.SchedulerCheckInterval;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync(
                "aetheus:ai-task-scheduler",
                RunLeaderLoopAsync,
                stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AI task scheduler tick failed; will retry next interval");
            }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAiTaskRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IAiTaskService>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var definition in await repo.GetScheduledDefinitionsAsync(ct).ConfigureAwait(false))
        {
            try
            {
                if (DueOccurrence(definition, now) is not { } occurrence)
                    continue;
                await service.RunNowAsync(definition.Id, null, ct).ConfigureAwait(false);
                definition.LastScheduledAt = occurrence;
                await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (CronFormatException ex)
            {
                logger.LogWarning(ex, "Invalid AI task schedule for definition {DefinitionId}", definition.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not schedule AI task definition {DefinitionId}", definition.Id);
            }
        }
    }

    /// <summary>The occurrence of the definition's schedule that falls in the last <see cref="Window"/>
    /// and has not run yet, or <c>null</c>.</summary>
    internal static DateTime? DueOccurrence(AiTaskDefinition definition, DateTime now)
    {
        var occurrence = AiTaskService.ParseSchedule(definition.Schedule)?.GetNextOccurrence(now - Window, inclusive: true);
        if (occurrence is null || occurrence > now)
            return null;
        return definition.LastScheduledAt is { } last && last >= occurrence ? null : occurrence;
    }
}
