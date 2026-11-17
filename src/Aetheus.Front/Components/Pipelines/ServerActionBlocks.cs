// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The server actions of the plan as blocks of the planner's calendar (recette R-8): one appointment per occurrence,
/// named by what it does and where, drawn apart from the pipelines by <see cref="BlockClass"/> and said in words (the
/// look never says it alone) when its duration is planned, when it overlaps another run or when it cannot be moved.
/// </summary>
internal static class ServerActionBlocks
{
    private const char Separator = '|';

    /// <summary>The identity of the block of one occurrence: the action's id (<c>rkhunter:7</c>), then its start in UTC.</summary>
    public static string AppointmentId(ScheduledServerActionDto action, DateTime startUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{action.Id}{Separator}{startUtc:O}");

    /// <summary>The action a block belongs to, or null when the appointment is not one of an action.</summary>
    public static ScheduledServerActionDto? Find(SchedulePlanDto plan, string appointmentId)
    {
        var separator = appointmentId.IndexOf(Separator, StringComparison.Ordinal);
        return separator > 0
            ? plan.ServerActions.FirstOrDefault(action => string.Equals(action.Id, appointmentId[..separator], StringComparison.Ordinal))
            : null;
    }

    public static IEnumerable<OmniSchedulerAppointment> Appointments(
        SchedulePlanDto plan, IStringLocalizer<AppStrings> localizer, Func<DateTime, DateTimeOffset> inZone) =>
        plan.ServerActions.SelectMany(action => action.Occurrences.Select(occurrence =>
            new OmniSchedulerAppointment(
                AppointmentId(action, occurrence.StartUtc), Title(localizer, action, occurrence, plan.ExecutionCapacity),
                inZone(occurrence.StartUtc), inZone(occurrence.EndUtc),
                SchedulePhrases.Duration(localizer, ServerActionPlannerTask.AsTask(action, string.Empty)))
            {
                Class = BlockClass(action, occurrence)
            }));

    private static string Title(IStringLocalizer<AppStrings> localizer, ScheduledServerActionDto action, ScheduleOccurrenceDto occurrence, int capacity)
    {
        var marks = new List<string>();
        if (action.IsPlanned) marks.Add(localizer["ScheduleBlockPlanned"].Value);
        if (occurrence.OverlapMinutes > 0)
            marks.Add(string.Format(localizer[capacity > 1 ? "ScheduleBlockOverCapacity" : "ScheduleBlockOverlap"].Value, SchedulePhrases.Minutes(occurrence.OverlapMinutes)));
        if (!IsMovable(action)) marks.Add(localizer["ScheduleBlockLocked"].Value);
        var title = ServerActionPlannerTask.Title(localizer, action);
        return marks.Count == 0 ? title : $"{title} ({string.Join(", ", marks)})";
    }

    /// <summary>Movable for the calendar: the reader may write it (the server says so, <see cref="ScheduledServerActionDto.Movable"/>) and the
    /// schedule is one time of day: a UTC time of a cron, or the local daily time of the CVE watch.</summary>
    public static bool IsMovable(ScheduledServerActionDto action) =>
        action.Movable && (action.Kind == ServerActionKind.CveWatch
            ? action.DailyTime is not null
            : action.Frequency != ScheduleFrequency.Custom && action.Hour is not null && action.Minute is not null);

    private static string BlockClass(ScheduledServerActionDto action, ScheduleOccurrenceDto occurrence) =>
        string.Join(' ', new[]
        {
            "schedule-block",
            "schedule-block--server",
            action.IsPlanned ? "schedule-block--planned" : null,
            occurrence.OverlapMinutes > 0 ? "schedule-block--overlap" : null,
            IsMovable(action) ? null : "schedule-block--locked"
        }.Where(name => name is not null));
}
