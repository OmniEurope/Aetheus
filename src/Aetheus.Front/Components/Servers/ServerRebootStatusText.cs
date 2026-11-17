// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers;

/// <summary>Recette R-8: the sentences that say a reboot schedule and what Aetheus last did with it (planned, done,
/// postponed, skipped, refused), for the planner and the server page. Times are written in UTC, said as such; the daily
/// time is written in the schedule's own zone, named.</summary>
internal static class ServerRebootStatusText
{
    public static string When(IStringLocalizer<AppStrings> l, ServerRebootScheduleDto schedule) =>
        string.Format(CultureInfo.CurrentCulture, l["ServerRebootDaily"].Value,
            string.Create(CultureInfo.InvariantCulture, $"{schedule.Hour:00}:{schedule.Minute:00}"), schedule.TimeZoneId);

    public static string Outcome(IStringLocalizer<AppStrings> l, ServerRebootScheduleDto schedule)
    {
        var at = Utc(schedule.LastOutcomeAtUtc);
        return schedule.LastOutcome switch
        {
            RebootScheduleOutcome.Postponed => Format(l, "ServerRebootPostponed", at, schedule.OutcomeDetail ?? string.Empty),
            RebootScheduleOutcome.Skipped when schedule.SkipReason == RebootSkipReason.ServerBusy =>
                Format(l, "ServerRebootSkippedBusy", at, schedule.OutcomeDetail ?? string.Empty),
            RebootScheduleOutcome.Skipped => Format(l, "ServerRebootSkippedMissed", at),
            RebootScheduleOutcome.Ordered => Ordered(l, schedule, at),
            _ => schedule.Enabled ? Planned(l, schedule) : l["ServerRebootDisabled"].Value
        };
    }

    public static string Planned(IStringLocalizer<AppStrings> l, ServerRebootScheduleDto schedule) =>
        schedule.NextRunUtc is { } next ? Format(l, "ServerRebootPlanned", Utc(next)) : l["ServerRebootDisabled"].Value;

    private static string Ordered(IStringLocalizer<AppStrings> l, ServerRebootScheduleDto schedule, string at) => schedule.LastTaskStatus switch
    {
        TaskExecutionStatus.Success => Format(l, "ServerRebootDone", Utc(schedule.LastTaskCompletedAtUtc ?? schedule.LastOutcomeAtUtc)),
        TaskExecutionStatus.Failed when Enum.TryParse<RebootOrderRefusal>(schedule.LastTaskFailureReason, out var refusal)
            && Enum.IsDefined(refusal) && refusal != RebootOrderRefusal.None =>
            Format(l, "ServerRebootRefused", at, l["ServerRebootRefusal" + refusal].Value),
        TaskExecutionStatus.Failed => Format(l, "ServerRebootFailed", at),
        TaskExecutionStatus.Timeout => Format(l, "ServerRebootUnknown", at),
        TaskExecutionStatus.Cancelled => Format(l, "ServerRebootCancelled", at),
        _ => Format(l, "ServerRebootOrdered", at)
    };

    private static string Format(IStringLocalizer<AppStrings> l, string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, l[key].Value, values);

    private static string Utc(DateTime? value) => value is { } at
        ? string.Create(CultureInfo.InvariantCulture, $"{DateTime.SpecifyKind(at, DateTimeKind.Utc):dd/MM/yyyy HH:mm} UTC")
        : string.Empty;
}
