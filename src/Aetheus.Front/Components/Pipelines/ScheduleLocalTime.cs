// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>A schedule as the reader's clock says it: the local time of the run and, for a weekly schedule, the
/// local day of the week (0 = Sunday) or, for a monthly one, the local day of the month.</summary>
internal readonly record struct LocalSchedule(TimeOnly Time, int? Day);

/// <summary>
/// The conversion between the engine's clock (UTC, what the YAML and the write contract carry) and the
/// reader's local clock (what the page shows and asks for). The offset is the one in force at the day the
/// run next happens, so a task keeps its UTC time all year and its local time moves by an hour at the
/// daylight-saving change, which the page's help says.
/// </summary>
internal static class ScheduleLocalTime
{
    /// <summary>The local view of a task, or null for an expression that is not a single time of day.</summary>
    public static LocalSchedule? ToLocal(ScheduledTaskDto task, TimeZoneInfo zone, DateTime nowUtc)
    {
        if (task.Hour is not { } hour || task.Minute is not { } minute) return null;
        var instant = task.Enabled && task.NextRunUtc is { } next
            ? AsUtc(next)
            : ReferenceInstant(task, hour, minute, nowUtc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
        var day = task.Frequency switch
        {
            ScheduleFrequency.Weekly => (int)local.DayOfWeek,
            ScheduleFrequency.Monthly => local.Day,
            _ => (int?)null
        };
        return new LocalSchedule(TimeOnly.FromDateTime(local), day);
    }

    /// <summary>The UTC proposal for a frequency asked in local terms (<paramref name="localDay"/> is the
    /// local day of the week for a weekly schedule, of the month for a monthly one), or null when the
    /// local day has no UTC counterpart the engine can fire every month (a monthly run near midnight
    /// whose UTC day falls out of 1 to 28), or a Monday to Friday run whose UTC day is not the local day.</summary>
    public static ScheduleFrequencySpec? ToUtc(
        ScheduleFrequency frequency, int? localDay, TimeOnly localTime, TimeZoneInfo zone, DateTime nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(nowUtc), zone);
        var today = DateOnly.FromDateTime(localNow);
        var anchor = frequency switch
        {
            ScheduleFrequency.Weekly when localDay is { } weekday => NextWeekday(today, weekday),
            // F-FRONT-004: the occurrence ToLocal shows (the next run), not the current month's day, whose
            // offset can be the other side of a daylight-saving change.
            ScheduleFrequency.Monthly when localDay is { } monthDay => NextMonthDay(localNow, monthDay, localTime),
            _ => today
        };
        return ToUtcOn(frequency, anchor, localTime, zone);
    }

    /// <summary>The UTC proposal for a run at <paramref name="localTime"/> on the local date
    /// <paramref name="anchor"/>: the offset in force on that day converts the time, and the UTC day of the week
    /// (weekly) or of the month (monthly) is read from the converted instant. Null when the proposal cannot be
    /// written (see <see cref="ToUtc"/>).</summary>
    public static ScheduleFrequencySpec? ToUtcOn(ScheduleFrequency frequency, DateOnly anchor, TimeOnly localTime, TimeZoneInfo zone)
    {
        var wall = anchor.ToDateTime(localTime, DateTimeKind.Unspecified);
        // A wall time the zone skips (the spring-forward gap) means the first instant after the gap.
        if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        var utc = TimeZoneInfo.ConvertTimeToUtc(wall, zone);
        int? day = frequency switch
        {
            ScheduleFrequency.Weekly => (int)utc.DayOfWeek,
            ScheduleFrequency.Monthly => utc.Day,
            _ => null
        };
        if (frequency == ScheduleFrequency.Monthly && day is < 1 or > ScheduleFrequencyCodec.MaxMonthDay) return null;
        // Monday to Friday is written in UTC days: a time that falls on another UTC day would run Tuesday to Saturday locally.
        if (frequency == ScheduleFrequency.Weekdays && DateOnly.FromDateTime(utc) != anchor) return null;
        return new ScheduleFrequencySpec(frequency, day, utc.Hour, utc.Minute);
    }

    /// <summary>The zone's name for the reader: the city of an IANA id ("Europe/Paris" gives "Paris"), the zone's own name otherwise.</summary>
    public static string ZoneName(TimeZoneInfo zone)
    {
        var slash = zone.Id.LastIndexOf('/');
        return slash >= 0 ? zone.Id[(slash + 1)..].Replace('_', ' ') : zone.StandardName;
    }

    private static DateTime ReferenceInstant(ScheduledTaskDto task, int hour, int minute, DateTime nowUtc)
    {
        var today = DateOnly.FromDateTime(AsUtc(nowUtc));
        var date = task.Frequency switch
        {
            ScheduleFrequency.Weekly when task.Day is { } weekday => NextWeekday(today, weekday),
            ScheduleFrequency.Monthly when task.Day is { } monthDay =>
                NextMonthDay(AsUtc(nowUtc), monthDay, new TimeOnly(hour, minute)),
            _ => today
        };
        return date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);
    }

    /// <summary>The date of the next run of a monthly schedule seen from <paramref name="now"/> (a wall time of the
    /// clock the day and time are read in): this month's day when its time has not passed yet, the next month's
    /// otherwise. The day is held to 1 to 28, the days the engine fires every month.</summary>
    private static DateOnly NextMonthDay(DateTime now, int day, TimeOnly time)
    {
        var thisMonth = new DateOnly(now.Year, now.Month, Math.Clamp(day, 1, ScheduleFrequencyCodec.MaxMonthDay));
        if (thisMonth.ToDateTime(time, DateTimeKind.Unspecified) > DateTime.SpecifyKind(now, DateTimeKind.Unspecified))
            return thisMonth;
        var next = thisMonth.AddMonths(1);
        return new DateOnly(next.Year, next.Month, thisMonth.Day);
    }

    private static DateOnly NextWeekday(DateOnly from, int weekday)
    {
        var ahead = ((weekday - (int)from.DayOfWeek) % 7 + 7) % 7;
        return from.AddDays(ahead);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
