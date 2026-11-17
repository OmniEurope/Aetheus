// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// What a drop on the calendar means for a schedule: <see cref="Refusal"/> (the resource key of the sentence that
/// says why nothing is written), a <see cref="Spec"/> to write (the UTC proposal, <see cref="LocalDay"/> and
/// <see cref="LocalTime"/> its words at the reader's clock), or neither (the drop changes nothing).
/// </summary>
internal sealed record ScheduleMoveDecision(
    string? Refusal,
    ScheduleFrequencySpec? Spec,
    int? LocalDay,
    TimeOnly LocalTime,
    bool DateIgnored)
{
    public bool IsWrite => Spec is not null;

    /// <summary>True for the frequencies whose runs all share one time: moving one run moves every run.</summary>
    public bool CommonTime { get; init; }
}

/// <summary>
/// The pure rules of a drop. A weekly schedule takes the day and the time of the drop. Every evening, Monday to
/// Friday, every two days and monthly have one time for all their runs: only the time of the drop is kept, its
/// date is ignored (the offset of the moved run's own day converts it). A custom expression, a task the reader
/// may not write and a time the engine cannot fire as written are refused, never written approximately; a drop
/// that gives the stored values back writes nothing. The zone and the answers are arguments, so the daylight
/// saving changes are tested with plain dates.
/// </summary>
internal static class ScheduleMovePlanner
{
    public static ScheduleMoveDecision Decide(
        ScheduledTaskDto task, DateTimeOffset originalStart, DateTimeOffset droppedStart, TimeZoneInfo zone, bool canWrite)
    {
        var dropped = TimeZoneInfo.ConvertTime(droppedStart, zone);
        var original = TimeZoneInfo.ConvertTime(originalStart, zone);
        var time = TimeOnly.FromDateTime(dropped.DateTime);
        var dropDate = DateOnly.FromDateTime(dropped.DateTime);
        var originalDate = DateOnly.FromDateTime(original.DateTime);
        if (!canWrite) return Refused("SchedulePermissionDenied", time);
        if (task.Frequency == ScheduleFrequency.Custom || task.Hour is null || task.Minute is null)
            return Refused("ScheduleMoveCustom", time);

        var weekly = task.Frequency == ScheduleFrequency.Weekly;
        // The weekly schedule moves to the dropped day; the others stay on the day of the run that was moved.
        var anchor = weekly ? dropDate : originalDate;
        var spec = ScheduleLocalTime.ToUtcOn(task.Frequency, anchor, time, zone);
        if (spec is null)
        {
            return Refused(task.Frequency == ScheduleFrequency.Weekdays ? "ScheduleWeekdaysImpossible" : "ScheduleMonthDayImpossible", time);
        }

        var localDay = task.Frequency switch
        {
            ScheduleFrequency.Weekly => (int)dropDate.DayOfWeek,
            ScheduleFrequency.Monthly => originalDate.Day,
            _ => (int?)null
        };
        var dateIgnored = !weekly && dropDate != originalDate;
        var unchanged = spec.Day == task.Day && spec.Hour == task.Hour && spec.Minute == task.Minute;
        return new ScheduleMoveDecision(null, unchanged ? null : spec, localDay, time, dateIgnored) { CommonTime = !weekly };
    }

    /// <summary>The request of a move: the new value, written against what the page read.</summary>
    public static UpdatePipelineScheduleRequest RequestOf(ScheduledTaskDto task, ScheduleFrequencySpec spec) => new()
    {
        Frequency = spec.Frequency,
        Day = spec.Day,
        Hour = spec.Hour,
        Minute = spec.Minute,
        Enabled = task.Enabled,
        ExpectedCronExpression = task.CronExpression,
        ExpectedEnabled = task.Enabled
    };

    /// <summary>The request that puts a task's current value back (what "Undo" writes), or null when the value
    /// cannot be written: a custom expression, or no single time of day.</summary>
    public static UpdatePipelineScheduleRequest? Previous(ScheduledTaskDto task)
    {
        if (task.Frequency == ScheduleFrequency.Custom || task.Hour is null || task.Minute is null) return null;
        return new UpdatePipelineScheduleRequest
        {
            Frequency = task.Frequency,
            Day = task.Day,
            Hour = task.Hour,
            Minute = task.Minute,
            Enabled = task.Enabled
        };
    }

    private static ScheduleMoveDecision Refused(string key, TimeOnly time) => new(key, null, null, time, false);
}
