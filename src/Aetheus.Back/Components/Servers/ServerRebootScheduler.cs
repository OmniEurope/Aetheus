// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Recette R-8 (option B of 2026-11-07): turns the due occurrence of each enabled reboot schedule into a reboot order for
/// the server's agent, through the typed server-action pipeline. An occurrence waits while a pipeline step or a server
/// action runs on the server (postponed, with the name of what runs), at most <see cref="ServerRebootOrder.MaxPostponement"/>;
/// past that it is skipped with its reason. An occurrence is settled once: ordered or skipped, never twice.
/// </summary>
internal sealed class ServerRebootScheduler(
    IServerRebootRepository repository, ITaskService taskService, IAuditService audit, TimeProvider clock,
    ILogger<ServerRebootScheduler> logger)
{
    /// <summary>How far back an occurrence is looked for: one day, the period of a daily schedule.</summary>
    private static readonly TimeSpan LookBack = TimeSpan.FromHours(24);

    private const int OrderTimeoutSeconds = 120;
    private const int DetailMaxLength = 200;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var schedules = await repository.GetEnabledAsync(ct).ConfigureAwait(false);
        foreach (var schedule in schedules)
        {
            await HandleAsync(schedule, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(ServerRebootSchedule schedule, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var floor = Max(schedule.UpdatedAtUtc, schedule.LastHandledOccurrenceUtc ?? DateTime.MinValue, now - LookBack);
        var due = ServerRebootOccurrences.Between(schedule, floor, now);
        if (due.Count == 0)
            return;

        var occurrence = due[^1];
        if (now - occurrence > ServerRebootOrder.MaxPostponement)
        {
            var wasPostponed = schedule.LastOutcome == RebootScheduleOutcome.Postponed && schedule.LastOccurrenceUtc == occurrence;
            await SkipAsync(schedule, occurrence, wasPostponed ? RebootSkipReason.ServerBusy : RebootSkipReason.Missed, now, ct)
                .ConfigureAwait(false);
            return;
        }

        var busy = await repository.FindBusyTaskNameAsync(schedule.ServerId, null, ct).ConfigureAwait(false);
        if (busy is not null)
        {
            await PostponeAsync(schedule, occurrence, busy, now, ct).ConfigureAwait(false);
            return;
        }

        await OrderAsync(schedule, occurrence, now, ct).ConfigureAwait(false);
    }

    private async Task PostponeAsync(ServerRebootSchedule schedule, DateTime occurrence, string busy, DateTime now, CancellationToken ct)
    {
        var first = schedule.LastOutcome != RebootScheduleOutcome.Postponed || schedule.LastOccurrenceUtc != occurrence;
        schedule.LastOutcome = RebootScheduleOutcome.Postponed;
        schedule.LastOccurrenceUtc = occurrence;
        schedule.SkipReason = RebootSkipReason.None;
        schedule.OutcomeDetail = Truncate(busy);
        if (first)
            schedule.LastOutcomeAtUtc = now;
        await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        if (first)
        {
            logger.LogInformation("Reboot of server {ServerId} postponed: {Task} is running", schedule.ServerId, busy);
            await audit.LogAsync("ServerRebootPostponed", ServerRebootScheduleService.AuditEntity, schedule.ServerId,
                Occurrence(occurrence, $"waiting for {schedule.OutcomeDetail}"), ct).ConfigureAwait(false);
        }
    }

    private async Task SkipAsync(ServerRebootSchedule schedule, DateTime occurrence, RebootSkipReason reason, DateTime now, CancellationToken ct)
    {
        schedule.LastHandledOccurrenceUtc = occurrence;
        schedule.LastOutcome = RebootScheduleOutcome.Skipped;
        schedule.LastOccurrenceUtc = occurrence;
        schedule.LastOutcomeAtUtc = now;
        schedule.SkipReason = reason;
        if (reason != RebootSkipReason.ServerBusy)
            schedule.OutcomeDetail = null;
        await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogWarning("Reboot of server {ServerId} skipped for {Occurrence:o}: {Reason}", schedule.ServerId, occurrence, reason);
        await audit.LogAsync("ServerRebootSkipped", ServerRebootScheduleService.AuditEntity, schedule.ServerId,
            Occurrence(occurrence, reason.ToString()), ct).ConfigureAwait(false);
    }

    private async Task OrderAsync(ServerRebootSchedule schedule, DateTime occurrence, DateTime now, CancellationToken ct)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServerRebootOrder.ScheduleVersionEnvVar] = schedule.Version.ToString(CultureInfo.InvariantCulture),
            [ServerRebootOrder.OccurrenceEnvVar] = occurrence.ToString("o", CultureInfo.InvariantCulture),
            [ServerRebootOrder.NotAfterEnvVar] = (occurrence + ServerRebootOrder.MaxPostponement).ToString("o", CultureInfo.InvariantCulture)
        };
        var order = ServerTaskFactory.Operation(schedule.ServerId, ServerTaskNames.ScheduledReboot, OperationKind.ServerReboot,
            target: "-", env, timeoutSeconds: OrderTimeoutSeconds);

        schedule.LastHandledOccurrenceUtc = occurrence;
        schedule.LastOutcome = RebootScheduleOutcome.Ordered;
        schedule.LastOccurrenceUtc = occurrence;
        schedule.LastOutcomeAtUtc = now;
        schedule.SkipReason = RebootSkipReason.None;
        schedule.OutcomeDetail = null;
        await repository.AddOrderAsync(schedule, order, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(order, ct: ct).ConfigureAwait(false);
        logger.LogInformation("Reboot of server {ServerId} ordered as task {TaskId}", schedule.ServerId, order.Id);
        await audit.LogAsync("ServerRebootOrdered", ServerRebootScheduleService.AuditEntity, schedule.ServerId,
            Occurrence(occurrence, string.Create(CultureInfo.InvariantCulture, $"task {order.Id}, schedule version {schedule.Version}")), ct)
            .ConfigureAwait(false);
    }

    private static string Occurrence(DateTime occurrence, string what) =>
        string.Create(CultureInfo.InvariantCulture, $"occurrence {occurrence:yyyy-MM-dd HH:mm} UTC: {what}");

    private static string Truncate(string value) => value.Length <= DetailMaxLength ? value : value[..DetailMaxLength];

    private static DateTime Max(DateTime a, DateTime b, DateTime c) =>
        new DateTime(Math.Max(Math.Max(a.Ticks, b.Ticks), c.Ticks), DateTimeKind.Utc);
}

/// <summary>Runs <see cref="ServerRebootScheduler"/> on the live colour only, every scheduler interval.</summary>
internal sealed class ServerRebootSchedulerService(
    IServiceScopeFactory scopeFactory, ILogger<ServerRebootSchedulerService> logger, TimeProvider timeProvider,
    IPostgresLeaderLease leaderLease) : LeaderBackgroundService(leaderLease, "aetheus:server-reboot-scheduler")
{
    protected override async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BackendRuntimeDefaults.SchedulerCheckInterval, timeProvider);
        do
        {
            await RunPassAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One pass, in its own scope. A failed pass is logged and retried at the next tick: a background
    /// service that throws would stop for good.</summary>
    internal async Task RunPassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ServerRebootScheduler>().RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Server reboot scheduler pass failed");
        }
    }
}
