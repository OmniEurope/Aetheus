// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Cronos;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Starts the pipelines whose cron schedule is due. Every occurrence is decided once: the scheduler takes
/// it in charge by moving <see cref="Pipeline.LastScheduledOccurrence"/> forward with one conditional
/// update, and only the instance that moved it acts. That holds across ticks (several can see the same
/// occurrence), restarts and replicas (the leader lease keeps one loop, the conditional update keeps one
/// winner even when two leaders overlap). The same cursor makes an occurrence missed during an outage
/// visible to the next tick, which catches it up. Occurrences are computed from UTC instants, so a local
/// daylight-saving change cannot move or repeat one.
/// </summary>
public sealed class PipelineSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<PipelineSchedulerService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease leaderLease) : LeaderBackgroundService(leaderLease, "aetheus:pipeline-scheduler")
{
    private static readonly TimeSpan CheckInterval = BackendRuntimeDefaults.SchedulerCheckInterval;

    /// <summary>The slack the engine has always allowed: a pipeline first seen, or saved, at an instant is
    /// owed the occurrence that fell up to this long before, and an occurrence started later than this after
    /// its instant is a catch-up and is marked as one.</summary>
    internal static readonly TimeSpan TickTolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The oldest occurrence a catch-up still starts. A restart, a deployment or a leader change cuts the
    /// scheduler for minutes, and an occurrence missed then is still worth running; a cut that lasts
    /// hours is a maintenance or an incident, and a job started at the end of it (a nightly backup or
    /// deployment at midday) would surprise the people who already ran it by hand or chose to skip it.
    /// Two hours covers a long maintenance window and nothing more. An older occurrence is logged at
    /// Information, never started. The bound is on the age of the occurrence, so an occurrence
    /// that fell inside the last two hours of a longer outage is still started (once, the latest one).
    /// </summary>
    internal static readonly TimeSpan MaxCatchUpAge = TimeSpan.FromHours(2);

    /// <summary>Recorded by the run as its <c>BUILD_TRIGGEREDBY</c> (the way a downstream run says what
    /// started it) when it is a catch-up; a regular scheduled run keeps the pipeline's own trigger.</summary>
    internal const string CatchUpTriggeredBy = "schedule:catch-up";
    internal const string TriggeredByVariable = "BUILD_TRIGGEREDBY";

    /// <summary>The instant of the missed occurrence a catch-up run stands for (round-trip UTC).</summary>
    internal const string OccurrenceVariable = "SCHEDULE_OCCURRENCE";

    private const string TriggerSource = "Scheduler";

    private readonly Dictionary<int, ScheduleEntry> schedules = [];

    // What the scheduled pipelines looked like when they were last loaded. Per process: only the leader
    // runs the loop, and a new leader starts with nothing loaded. The database is the one source of truth,
    // so a change written by any replica shows in the fingerprint of the next tick.
    private ScheduledPipelinesFingerprint? loadedFingerprint;
    private List<Pipeline> loadedPipelines = [];

    protected override async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PipelineSchedulerService started");

        // Another instance may have led, and moved the cursors, since this process last did.
        loadedFingerprint = null;

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

        var (scheduledPipelines, reloaded) = await LoadScheduledPipelinesAsync(pipelineRepo, ct).ConfigureAwait(false);
        if (reloaded) ForgetPipelinesNotIn(scheduledPipelines);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Pre-load all pipeline IDs with active runs in a single query to avoid N+1.
        var activePipelineIds = await pipelineRepo.GetPipelineIdsWithActiveRunsAsync(ct).ConfigureAwait(false);

        foreach (var pipeline in scheduledPipelines)
        {
            try
            {
                await CheckPipelineAsync(pipeline, reloaded, now, activePipelineIds, pipelineRepo, runService, ct)
                    .ConfigureAwait(false);
            }
            catch (CronFormatException ex)
            {
                logger.LogWarning(ex, "Invalid cron expression in pipeline {PipelineId}", pipeline.Id);
            }
            catch (BadRequestException ex)
            {
                // Logs at Information because the refusal is already visible elsewhere: a refused launch that
                // reached the configuration checks was recorded by PipelineRefusedLaunchRecorder as a failed run
                // carrying the reasons, and its project's followers were notified (R-30). This log line is NOT
                // that record. The occurrence counts as handled: it was taken before the start, and it is not
                // retried.
                logger.LogInformation(
                    ex,
                    "Scheduled pipeline {PipelineId} ({PipelineName}) was refused by configuration validation",
                    pipeline.Id,
                    pipeline.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One pipeline failing (its occurrence was given back, so the next tick owes it again) never
                // stops the pipelines after it in the same tick (R-30).
                logger.LogError(
                    ex, "Scheduled pipeline {PipelineId} ({PipelineName}) failed to start; the other pipelines continue",
                    pipeline.Id, pipeline.Name);
            }
        }
    }

    private async Task CheckPipelineAsync(
        Pipeline pipeline, bool reloaded, DateTime now, HashSet<int> activePipelineIds,
        IPipelineAutoTriggerRepository pipelineRepo, IPipelineRunService runService, CancellationToken ct)
    {
        var entry = GetScheduleEntry(pipeline.Id, pipeline.YamlDefinition);
        if (entry.Cron is null) return;
        if (reloaded) entry.Cursor = Latest(entry.Cursor, AsUtc(pipeline.LastScheduledOccurrence));

        if (entry.Cursor is null)
        {
            // First time the scheduler sees this pipeline: what it owes starts at the usual slack before now,
            // and the baseline is written so that a restart knows from where it has to catch up.
            var baseline = now - TickTolerance;
            if (!await pipelineRepo.TryAdvanceScheduleCursorAsync(pipeline.Id, baseline, ct).ConfigureAwait(false))
            {
                loadedFingerprint = null;
                return;
            }

            entry.Cursor = baseline;
        }

        // An occurrence before the last save of the definition belongs to the definition it replaced.
        var saved = AsUtc(pipeline.UpdatedAt);
        var floor = Latest(entry.Cursor, saved > DateTime.MinValue + TickTolerance ? saved - TickTolerance : DateTime.MinValue)!.Value;
        var oldest = Latest(floor, now - MaxCatchUpAge)!.Value;
        if (oldest > floor)
        {
            if (await DropTooOldAsync(pipeline, entry, floor, oldest, pipelineRepo, runService, ct).ConfigureAwait(false) is null) return;
        }

        var due = LastOccurrence(entry.Cron, oldest, now);
        if (due is null) return;

        // Take the occurrence before acting on it: of several ticks, restarts or instances that see it,
        // exactly one gets here.
        var previous = entry.Cursor;
        if (!await pipelineRepo.TryAdvanceScheduleCursorAsync(pipeline.Id, due.Value, ct).ConfigureAwait(false))
        {
            loadedFingerprint = null;
            return;
        }

        entry.Cursor = due;

        // Check if there's already a running run to avoid duplicate triggers
        if (activePipelineIds.Contains(pipeline.Id))
        {
            logger.LogInformation(
                "Scheduled occurrence {Occurrence:o} of pipeline {PipelineId} ({PipelineName}) was skipped: a run is still active",
                due.Value, pipeline.Id, pipeline.Name);
            return;
        }

        var catchUp = now - due.Value > TickTolerance;
        if (catchUp)
            logger.LogInformation(
                "Catching up scheduled pipeline {PipelineId} ({PipelineName}): occurrence {Occurrence:o} was missed",
                pipeline.Id, pipeline.Name, due.Value);
        else
            logger.LogInformation("Triggering scheduled pipeline {PipelineId} ({PipelineName})", pipeline.Id, pipeline.Name);

        try
        {
            // F-EXEC-1b: scheduler has no caller principal - TriggerAutomatedRunAsync
            // authorizes the pipeline owner and fail-closes if unowned/under-privileged.
            await runService.TriggerAutomatedRunAsync(
                pipeline.Id, TriggerSource, catchUp ? CatchUpVariables(due.Value) : null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not BadRequestException)
        {
            // Nothing says the occurrence itself is at fault (database down, shutdown): give it back, so the
            // next tick starts it. A refusal (BadRequestException) is the occurrence's own answer and stays taken.
            await ReleaseAsync(pipeline, entry, due.Value, previous, pipelineRepo).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Occurrences between <paramref name="floor"/> and <paramref name="oldest"/> are too old to be
    /// caught up: they are logged and taken, so they are neither run nor reported again. Returns the new
    /// floor, or null when another instance moved the cursor first.</summary>
    private async Task<DateTime?> DropTooOldAsync(
        Pipeline pipeline, ScheduleEntry entry, DateTime floor, DateTime oldest,
        IPipelineAutoTriggerRepository pipelineRepo, IPipelineRunService runService, CancellationToken ct)
    {
        DateTime? first = null;
        DateTime? last = null;
        var count = 0;
        foreach (var occurrence in entry.Cron!.GetOccurrences(floor, oldest, fromInclusive: false, toInclusive: true))
        {
            first ??= occurrence;
            last = occurrence;
            count++;
        }

        if (last is null) return floor;

        logger.LogInformation(
            "Pipeline {PipelineId} ({PipelineName}): {Count} scheduled occurrence(s) from {First:o} to {Last:o} were missed " +
            "for longer than the catch-up limit of {Limit} and are not run",
            pipeline.Id, pipeline.Name, count, first, last, MaxCatchUpAge);
        if (!await pipelineRepo.TryAdvanceScheduleCursorAsync(pipeline.Id, last.Value, ct).ConfigureAwait(false))
        {
            loadedFingerprint = null;
            return null;
        }

        entry.Cursor = last;
        await RecordDroppedAsync(pipeline, runService, count, first!.Value, last.Value, ct).ConfigureAwait(false);
        return last.Value;
    }

    /// <summary>A log line is not visible to anyone: the dropped occurrences leave a failed run with the reason
    /// and the followers are told (R-30). Only the instance that took the occurrences records it, and a failure
    /// to record never blocks the occurrence that is due now.</summary>
    private async Task RecordDroppedAsync(
        Pipeline pipeline, IPipelineRunService runService, int count, DateTime first, DateTime last, CancellationToken ct)
    {
        var reason = string.Create(
            CultureInfo.InvariantCulture,
            $"{count} scheduled occurrence(s) from {first:yyyy-MM-dd HH:mm} to {last:yyyy-MM-dd HH:mm} UTC were missed for longer than "
            + $"the catch-up limit of {MaxCatchUpAge.TotalHours:0.##} h and were not run.");
        // The same reason as a problem code: the run page says it in the user's language, singular or plural;
        // the English above stays as its fallback and for the audit and the notification.
        var problem = PipelineProblem.Of(
            count == 1 ? PipelineProblemCodes.ScheduleOccurrenceMissed : PipelineProblemCodes.ScheduleOccurrencesMissed,
            reason,
            ("count", count),
            ("first", first.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            ("last", last.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            ("limit", MaxCatchUpAge.TotalHours.ToString("0.##", CultureInfo.InvariantCulture)));
        try
        {
            await runService.RecordRefusedAutomatedLaunchAsync(
                pipeline.Id, TriggerSource, reason, null, ct, [problem.ToDto()]).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record the dropped occurrences of pipeline {PipelineId}", pipeline.Id);
        }
    }

    private async Task ReleaseAsync(
        Pipeline pipeline, ScheduleEntry entry, DateTime claimed, DateTime? previous,
        IPipelineAutoTriggerRepository pipelineRepo)
    {
        try
        {
            await pipelineRepo.RestoreScheduleCursorAsync(pipeline.Id, claimed, previous, CancellationToken.None)
                .ConfigureAwait(false);
            entry.Cursor = previous;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Could not give back occurrence {Occurrence:o} of pipeline {PipelineId}; it will not be retried",
                claimed, pipeline.Id);
        }
    }

    private static Dictionary<string, string> CatchUpVariables(DateTime occurrence) => new()
    {
        [TriggeredByVariable] = CatchUpTriggeredBy,
        [OccurrenceVariable] = occurrence.ToString("o", CultureInfo.InvariantCulture)
    };

    /// <summary>The latest occurrence after <paramref name="after"/> up to and including <paramref name="until"/>.</summary>
    private static DateTime? LastOccurrence(CronExpression cron, DateTime after, DateTime until)
    {
        if (after >= until) return null;
        DateTime? last = null;
        foreach (var occurrence in cron.GetOccurrences(after, until, fromInclusive: false, toInclusive: true))
            last = occurrence;
        return last;
    }

    private static DateTime? Latest(DateTime? left, DateTime? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;

    private static DateTime? AsUtc(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>The scheduled pipelines, reloaded only when the fingerprint says they changed. The
    /// fingerprint is read first: a change landing between the two queries is caught by the next tick.
    /// <c>Reloaded</c> tells the caller the list (and the cursors it carries) was just read.</summary>
    private async Task<(List<Pipeline> Pipelines, bool Reloaded)> LoadScheduledPipelinesAsync(
        IPipelineAutoTriggerRepository pipelineRepo, CancellationToken ct)
    {
        var fingerprint = await pipelineRepo.GetScheduledPipelinesFingerprintAsync(ct).ConfigureAwait(false);
        if (loadedFingerprint == fingerprint) return (loadedPipelines, false);

        loadedPipelines = await pipelineRepo.GetScheduledPipelinesAsync(ct).ConfigureAwait(false);
        loadedFingerprint = fingerprint;
        return (loadedPipelines, true);
    }

    /// <summary>A pipeline that was deleted, or is no longer scheduled, leaves nothing behind.</summary>
    private void ForgetPipelinesNotIn(List<Pipeline> scheduledPipelines)
    {
        var kept = scheduledPipelines.Select(pipeline => pipeline.Id).ToHashSet();
        foreach (var id in schedules.Keys.Where(id => !kept.Contains(id)).ToList())
            schedules.Remove(id);
    }

    private ScheduleEntry GetScheduleEntry(int pipelineId, string yaml)
    {
        var yamlHash = StringComparer.Ordinal.GetHashCode(yaml);
        schedules.TryGetValue(pipelineId, out var cached);
        if (cached is not null && cached.YamlHash == yamlHash)
            return cached;

        var schedule = ExtractSchedule(yaml);
        var cron = schedule is null ? null : CronExpression.Parse(schedule, CronFormat.IncludeSeconds);
        var entry = new ScheduleEntry(yamlHash, cron) { Cursor = cached?.Cursor };
        schedules[pipelineId] = entry;
        return entry;
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

    /// <summary>What the engine remembers of one pipeline: the parsed schedule, and the cursor it knows
    /// (the database holds the authoritative one).</summary>
    private sealed class ScheduleEntry(int yamlHash, CronExpression? cron)
    {
        public int YamlHash { get; } = yamlHash;

        public CronExpression? Cron { get; } = cron;

        public DateTime? Cursor { get; set; }
    }
}
