// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Aetheus.Back.Services;
using Cronos;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Pipelines;

public sealed class PipelineSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<PipelineSchedulerService> logger,
    IMemoryCache memoryCache,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = BackendRuntimeDefaults.SchedulerCheckInterval;
    private const string ScheduledPipelinesCacheKey = "scheduler:pipelines";
    private readonly Dictionary<int, (int YamlHash, CronExpression? Cron)> scheduleCache = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (leaderLease is null)
            await RunLeaderLoopAsync(stoppingToken).ConfigureAwait(false);
        else
            await leaderLease.RunAsLeaderAsync("aetheus:pipeline-scheduler", RunLeaderLoopAsync, stoppingToken).ConfigureAwait(false);
    }

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PipelineSchedulerService started");

        using var timer = new PeriodicTimer(CheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CheckScheduledPipelinesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking scheduled pipelines");
            }
        }
    }

    internal async Task CheckScheduledPipelinesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var pipelineRepo = scope.ServiceProvider.GetRequiredService<IPipelineAutoTriggerRepository>();
        var runService = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();

        var scheduledPipelines = await memoryCache.GetOrCreateAsync(ScheduledPipelinesCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await pipelineRepo.GetScheduledPipelinesAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (scheduledPipelines is null) return;

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Pre-load all pipeline IDs with active runs in a single query to avoid N+1.
        var activePipelineIds = await pipelineRepo.GetPipelineIdsWithActiveRunsAsync(ct).ConfigureAwait(false);

        foreach (var pipeline in scheduledPipelines)
        {
            try
            {
                var cron = GetCachedCronExpression(pipeline.Id, pipeline.YamlDefinition);
                if (cron is null) continue;
                var nextOccurrence = cron.GetNextOccurrence(now.AddMinutes(-1), inclusive: true);

                if (nextOccurrence is null) continue;

                // Check if the next occurrence is within our check window
                if (nextOccurrence.Value <= now && nextOccurrence.Value > now.AddMinutes(-1))
                {
                    // Check if there's already a running run to avoid duplicate triggers
                    if (activePipelineIds.Contains(pipeline.Id)) continue;

                    logger.LogInformation("Triggering scheduled pipeline {PipelineId} ({PipelineName})",
                        pipeline.Id, pipeline.Name);

                    // F-EXEC-1b: scheduler has no caller principal - TriggerAutomatedRunAsync
                    // authorizes the pipeline owner and fail-closes if unowned/under-privileged.
                    await runService.TriggerAutomatedRunAsync(pipeline.Id, "Scheduler", ct: ct).ConfigureAwait(false);
                }
            }
            catch (CronFormatException ex)
            {
                logger.LogWarning(ex, "Invalid cron expression in pipeline {PipelineId}", pipeline.Id);
            }
            catch (BadRequestException ex)
            {
                // Logs at Information because the failed run records the reasons and preflight
                // already warns about each refusal.
                logger.LogInformation(
                    ex,
                    "Scheduled pipeline {PipelineId} ({PipelineName}) was refused by configuration validation",
                    pipeline.Id,
                    pipeline.Name);
            }
        }
    }

    private CronExpression? GetCachedCronExpression(int pipelineId, string yaml)
    {
        var yamlHash = StringComparer.Ordinal.GetHashCode(yaml);
        if (scheduleCache.TryGetValue(pipelineId, out var cached) && cached.YamlHash == yamlHash)
            return cached.Cron;

        var schedule = ExtractSchedule(yaml);
        if (schedule is null)
        {
            scheduleCache[pipelineId] = (yamlHash, null);
            return null;
        }

        var cron = CronExpression.Parse(schedule, CronFormat.IncludeSeconds);
        scheduleCache[pipelineId] = (yamlHash, cron);
        return cron;
    }

    private static string? ExtractSchedule(string yaml)
    {
        try
        {
            var def = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml);
            return string.IsNullOrEmpty(def?.Schedule) ? null : def.Schedule;
        }
        catch
        {
            return null;
        }
    }
}
