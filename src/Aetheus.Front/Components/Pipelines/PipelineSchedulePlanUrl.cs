// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The planner's view and day as they live in the URL (recette R-46): <c>?view=night|day|week|cards&amp;date=</c> with an ISO day,
/// so the browser's back button returns to the previous view or day. A missing or unreadable value means
/// "not chosen": the planner then shows its own default, so a clean URL stays valid.
/// </summary>
internal static class PipelineSchedulePlanUrl
{
    internal const string ViewKey = "view";
    internal const string DateKey = "date";

    /// <summary>The view named by the URL, or null when absent or not one of the four views.</summary>
    internal static string? ParseView(string? value) => value switch
    {
        SchedulePlanViewNames.Night => SchedulePlanViewNames.Night,
        SchedulePlanViewNames.Day => SchedulePlanViewNames.Day,
        SchedulePlanViewNames.Week => SchedulePlanViewNames.Week,
        SchedulePlanViewNames.Cards => SchedulePlanViewNames.Cards,
        _ => null
    };

    /// <summary>The day named by the URL (a calendar day, no time), or null when absent or malformed.</summary>
    internal static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    internal static string FormatDate(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The day, in the reader's zone, at midday: a time that exists on every day, whatever the offset change.</summary>
    internal static DateTimeOffset AtMidday(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(12, 0));
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The calendar day of an instant in the reader's zone.</summary>
    internal static DateOnly DayOf(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
