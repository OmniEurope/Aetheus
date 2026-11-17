// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The span of the plan the page reads, and the span a view of the calendar shows (recette R-43). The page reads
/// from the start of the week (or of the night in progress, whichever is earlier) to a few days ahead, so that the
/// night, day and week the reader opens are all inside it: the calendar then draws from the page's plan and a new
/// plan after a write needs no second read of its own range. Pure: the zone, the clock and the first day of the
/// week are arguments.
/// </summary>
internal static class SchedulePlanWindow
{
    /// <summary>Taken before the first midnight of the window: a clock change can shift a local midnight by an hour,
    /// and the span the scheduler shows must still be inside the window.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromHours(2);

    /// <summary>The window the page asks the server for: from the local midnight starting the week of now, or the
    /// evening of the night in progress when that is earlier, to <paramref name="lookAheadDays"/> days after now.</summary>
    internal static (DateTime FromUtc, DateTime ToUtc) For(DateTime nowUtc, TimeZoneInfo zone, int lookAheadDays, DayOfWeek firstDay)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone));
        var evening = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(SchedulePlanSummary.Night(zone, nowUtc).StartUtc, zone));
        var weekStart = WeekStart(today, firstDay);
        return (MidnightUtc(weekStart < evening ? weekStart : evening, zone) - Margin, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).AddDays(lookAheadDays));
    }

    /// <summary>The UTC span the scheduler asks for in a view on a local day: the night (from midnight to the
    /// morning after, <paramref name="nightEnd"/>), the whole day, or the week of that day. This mirrors the range
    /// OmniScheduler loads, which OE does not expose.</summary>
    internal static (DateTime StartUtc, DateTime EndUtc) Visible(string mode, DateOnly day, TimeZoneInfo zone, DayOfWeek firstDay, TimeOnly nightEnd)
    {
        switch (mode)
        {
            case SchedulePlanViewNames.Week:
                var start = WeekStart(day, firstDay);
                return (MidnightUtc(start, zone), MidnightUtc(start.AddDays(7), zone));
            case SchedulePlanViewNames.Night:
                return (MidnightUtc(day, zone), MidnightUtc(day.AddDays(1), zone) + nightEnd.ToTimeSpan());
            default:
                return (MidnightUtc(day, zone), MidnightUtc(day.AddDays(1), zone));
        }
    }

    /// <summary>True when the plan the page read holds the whole span.</summary>
    internal static bool Covers(SchedulePlanDto plan, (DateTime StartUtc, DateTime EndUtc) span) =>
        plan.FromUtc <= span.StartUtc && span.EndUtc <= plan.ToUtc;

    private static DateOnly WeekStart(DateOnly day, DayOfWeek firstDay) => day.AddDays(-((7 + (int)day.DayOfWeek - (int)firstDay) % 7));

    private static DateTime MidnightUtc(DateOnly day, TimeZoneInfo zone)
    {
        var wall = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(zone.IsInvalidTime(wall) ? wall.AddHours(1) : wall, zone);
    }
}
