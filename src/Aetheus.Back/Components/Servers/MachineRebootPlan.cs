// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Recette R-11: the planner rows of the reboots a machine decides by itself, from what its agent last reported. Each
/// entry gives one read-only row, with the occurrences of its expression read in the zone the agent named (the machine's,
/// or the one the entry carries), or the single instant of a reboot already scheduled on the host. An entry without a time
/// (unattended-upgrades set to reboot "now"), in a zone this host does not know, or with an expression the engine cannot
/// read gives no row: a time is shown only when it was read, never guessed. The times come from <see cref="MachineSchedule"/>.
/// </summary>
internal static class MachineRebootPlan
{
    public static IEnumerable<ScheduledServerActionDto> Build(MachineRebootRow row, DateTime fromUtc, DateTime toUtc, DateTime now)
    {
        if (MachineRebootRecorder.Read(row.Json) is not { Collected: true } data) yield break;
        for (var index = 0; index < data.Entries.Count && index < MachineRebootLimits.MaxEntries; index++)
        {
            if (Build(row, data.Entries[index], index, fromUtc, toUtc, now) is { } action) yield return action;
        }
    }

    private static ScheduledServerActionDto? Build(
        MachineRebootRow row, MachineRebootEntryDto entry, int index, DateTime fromUtc, DateTime toUtc, DateTime now)
    {
        var plan = entry.AtUtc is { } at
            ? MachineSchedule.Instant(at, fromUtc, toUtc, now)
            : MachineSchedule.Expression(entry.CronExpression, entry.TimeZoneId, fromUtc, toUtc, now);
        if (plan is null) return null;

        var daily = MachineSchedule.DailyTime(entry.CronExpression);
        return new ScheduledServerActionDto
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"machine-reboot:{row.ServerId}:{index}"),
            Kind = ServerActionKind.MachineReboot,
            // One row per entry: the row id of the planner is built from the source id, so two entries of a server differ.
            SourceId = (row.ServerId * MachineRebootLimits.MaxEntries) + index,
            ServerId = row.ServerId,
            ServerName = row.ServerName,
            CronExpression = entry.CronExpression ?? string.Empty,
            Frequency = ScheduleFrequency.Custom,
            DailyTime = daily,
            DailyTimeZoneId = daily is null ? null : entry.TimeZoneId,
            DurationMinutes = ServerRebootOrder.PlannedMinutes,
            IsPlanned = true,
            NextRunUtc = plan.Next,
            Occurrences = [.. plan.Starts.Select(start => new ScheduleOccurrenceDto { StartUtc = start, EndUtc = start.AddMinutes(ServerRebootOrder.PlannedMinutes) })],
            OccurrencesTruncated = plan.Truncated,
            Movable = false,
            MachineReboot = entry
        };
    }
}
