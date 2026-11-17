// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Inventory;

public interface IProjectInventoryTrigger
{
    /// <summary>Asks for a new read of a project, off the caller's request. Does nothing when the watch is off.</summary>
    void Request(int projectId);
}

/// <summary>Queues the read: it must neither delay a push nor the pipeline handlers of the same event.</summary>
internal sealed class ProjectInventoryTrigger(IBackgroundTaskQueue queue, IOptions<VulnerabilityWatchOptions> options)
    : IProjectInventoryTrigger
{
    public void Request(int projectId)
    {
        if (!options.Value.Enabled) return;
        queue.Enqueue((services, ct) =>
            services.GetRequiredService<IProjectInventoryService>().RefreshProjectAsync(projectId, ct));
    }
}

/// <summary>
/// The daily control pass: reads each project with no inventory or one older than
/// <see cref="VulnerabilityWatchOptions.InventoryMaxAgeHours"/>. It is what catches a mirror that moved, a
/// release, and a request lost with a restart. The age is stored, so a deployment never restarts it. It runs once a day at
/// the time of the CVE watch's daily pass (<see cref="VulnerabilityWatchOptions.PassTime"/>, recette R-3).
/// </summary>
public sealed class ProjectInventoryRefreshService(
    IServiceScopeFactory scopeFactory,
    IOptions<VulnerabilityWatchOptions> options,
    TimeProvider timeProvider,
    ILogger<ProjectInventoryRefreshService> logger,
    IPostgresLeaderLease leaderLease,
    IVulnerabilityPassScheduleSource? passSource = null)
    : PeriodicBackgroundService(TimeSpan.FromDays(1), leaderLease, LeaseName, timeProvider)
{
    private const string LeaseName = "aetheus:project-inventory";

    /// <summary>The control pass runs once a day: an inventory read at the previous pass is a few minutes under the maximum
    /// age when the next one comes, and would wait a whole further day. The slack makes it due.</summary>
    private static readonly TimeSpan PassSlack = TimeSpan.FromHours(1);

    /// <summary>How often the loop reads the pass time again while it waits.</summary>
    private static readonly TimeSpan ScheduleRecheck = TimeSpan.FromMinutes(1);

    private readonly VulnerabilityWatchOptions _options = options.Value;

    /// <summary>Sleeps until the next daily occurrence, whatever the time the leader started or the last pass ended.</summary>
    protected override async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        var schedule = await ReadScheduleAsync(_options.PassSchedule, stoppingToken).ConfigureAwait(false);
        var target = schedule.NextAfter(Clock.GetUtcNow().UtcDateTime);
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = target - Clock.GetUtcNow().UtcDateTime;
            // Never asleep for more than a minute at a time: the pass time an administrator moves in the planner is read
            // again at each wake, and the occurrence is recomputed when it changed.
            if (wait > ScheduleRecheck)
            {
                await Task.Delay(ScheduleRecheck, Clock, stoppingToken).ConfigureAwait(false);
                var current = await ReadScheduleAsync(schedule, stoppingToken).ConfigureAwait(false);
                if (current.Time != schedule.Time)
                {
                    schedule = current;
                    target = schedule.NextAfter(Clock.GetUtcNow().UtcDateTime);
                }

                continue;
            }

            if (wait > TimeSpan.Zero) await Task.Delay(wait, Clock, stoppingToken).ConfigureAwait(false);
            if (!await RunIterationAsync(stoppingToken).ConfigureAwait(false)) break;
            var now = Clock.GetUtcNow().UtcDateTime;
            // After the occurrence just served, so that a timer that woke a few ticks early never runs it twice.
            target = schedule.NextAfter(now < target ? target : now);
        }
    }

    private Task<VulnerabilityPassSchedule> ReadScheduleAsync(VulnerabilityPassSchedule fallback, CancellationToken ct) =>
        passSource.GetOrKeepAsync(fallback, logger, ct);

    protected override Task ExecuteIterationAsync(CancellationToken ct) =>
        _options.Enabled ? RunPassAsync(ct) : Task.CompletedTask;

    protected override void LogIterationError(Exception exception) =>
        logger.LogError(exception, "Project inventory pass failed");

    /// <summary>Each project has its own scope, and one that fails never stops the others: it stays due.</summary>
    internal async Task RunPassAsync(CancellationToken ct)
    {
        IReadOnlyList<int> due;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var cutoff = Clock.GetUtcNow().UtcDateTime.AddHours(-_options.InventoryMaxAgeHours) + PassSlack;
            due = await scope.ServiceProvider.GetRequiredService<IProjectInventoryRepository>()
                .GetDueProjectIdsAsync(cutoff, ct).ConfigureAwait(false);
        }

        foreach (var projectId in due)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IProjectInventoryService>()
                    .RefreshProjectAsync(projectId, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Inventory of project {ProjectId} failed", projectId);
            }
        }
    }
}
