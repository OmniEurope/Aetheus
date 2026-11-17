// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Recette R-27: the planner rows of the jobs a machine schedules by itself (crontabs, run-parts directories, whitelisted
/// systemd timers), from what its agent last reported. Each job gives one read-only row, with the occurrences of its expression
/// read in the zone the agent named (the job's own, else the machine's), or the single next instant of a timer that has no
/// calendar. A job whose zone this host does not know, or whose expression the engine cannot read, gives no row: a time is shown
/// only when it was read, never guessed. A job runs on the host, never on a runner of Aetheus: it is drawn and never counted in
/// the overlaps. The times come from <see cref="MachineSchedule"/>.
/// </summary>
internal static class MachineJobPlan
{
    public static IEnumerable<ScheduledServerActionDto> Build(MachineJobsRow row, DateTime fromUtc, DateTime toUtc, DateTime now)
    {
        if (MachineJobsRecorder.Read(row.Json) is not { Collected: true } data) yield break;
        for (var index = 0; index < data.Jobs.Count && index < MachineJobLimits.MaxEntries; index++)
        {
            if (Build(row, data, data.Jobs[index], index, fromUtc, toUtc, now) is { } action) yield return action;
        }
    }

    private static ScheduledServerActionDto? Build(
        MachineJobsRow row, MachineJobsDataDto data, MachineJobEntryDto job, int index, DateTime fromUtc, DateTime toUtc, DateTime now)
    {
        // A job the agent read in no zone of its own is read in the machine's: that is why the agent sends it.
        var zoneId = job.TimeZoneId ?? data.TimeZoneId;
        var plan = job.CronExpression is not null
            ? MachineSchedule.Expression(job.CronExpression, zoneId, fromUtc, toUtc, now)
            : job.AtUtc is { } at ? MachineSchedule.Instant(at, fromUtc, toUtc, now) : null;
        if (plan is null) return null;

        var daily = MachineSchedule.DailyTime(job.CronExpression);
        var duration = ServerActionDefaults.MachineJobPlannedMinutes;
        return new ScheduledServerActionDto
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"machine-job:{row.ServerId}:{index}"),
            Kind = ServerActionKind.MachineJob,
            // One row per job: the row id of the planner is built from the source id, so two jobs of a server differ.
            SourceId = (row.ServerId * MachineJobLimits.MaxEntries) + index,
            SourceName = job.Name,
            ServerId = row.ServerId,
            ServerName = row.ServerName,
            CronExpression = job.CronExpression ?? string.Empty,
            Frequency = ScheduleFrequency.Custom,
            DailyTime = daily,
            DailyTimeZoneId = daily is null ? null : zoneId,
            DurationMinutes = duration,
            IsPlanned = true,
            NextRunUtc = plan.Next,
            Occurrences = [.. plan.Starts.Select(start => new ScheduleOccurrenceDto { StartUtc = start, EndUtc = start.AddMinutes(duration) })],
            OccurrencesTruncated = plan.Truncated,
            Movable = false,
            MachineJob = job with { TimeZoneId = zoneId }
        };
    }
}
