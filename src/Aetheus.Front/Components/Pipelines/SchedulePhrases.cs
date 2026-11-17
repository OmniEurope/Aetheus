// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The sentences of the build planner: a frequency in words, a duration, a time. A frequency is
/// never shown as its enum name or its cron expression (except a custom one, shown as written because it
/// has no words), and every text goes through the localizer.</summary>
internal static class SchedulePhrases
{
    /// <summary>"Every evening at 01:00", "Once a week, on Monday, at 02:00"... for a local time and day.</summary>
    public static string Frequency(IStringLocalizer<AppStrings> localizer, ScheduleFrequency frequency, int? localDay, TimeOnly time)
    {
        var at = Time(time);
        return frequency switch
        {
            ScheduleFrequency.EveryEvening => string.Format(localizer["ScheduleEveryEvening"].Value, at),
            ScheduleFrequency.Weekdays => string.Format(localizer["ScheduleWeekdays"].Value, at),
            ScheduleFrequency.EveryTwoDays => string.Format(localizer["ScheduleEveryTwoDays"].Value, at),
            ScheduleFrequency.Weekly => string.Format(localizer["ScheduleWeekly"].Value, DayName(localDay ?? 0), at),
            ScheduleFrequency.Monthly => string.Format(localizer["ScheduleMonthly"].Value, localDay ?? 1, at),
            _ => localizer["ScheduleCustom"].Value
        };
    }

    /// <summary>The frequency of a task at the reader's clock; a custom expression is its own text.</summary>
    public static string Frequency(IStringLocalizer<AppStrings> localizer, ScheduledTaskDto task, LocalSchedule? local) =>
        task.Frequency == ScheduleFrequency.Custom || local is null
            ? localizer["ScheduleCustom"].Value
            : Frequency(localizer, task.Frequency, local.Value.Day, local.Value.Time);

    /// <summary>"i.e. 00:00 UTC": what the engine reads, shown beside the local time.</summary>
    public static string Utc(IStringLocalizer<AppStrings> localizer, ScheduledTaskDto task) =>
        task.Hour is { } hour && task.Minute is { } minute
            ? string.Format(localizer["ScheduleUtcSecondary"].Value, Time(new TimeOnly(hour, minute)))
            : string.Empty;

    public static string Duration(IStringLocalizer<AppStrings> localizer, ScheduledTaskDto task) =>
        string.Format(localizer[task.IsPlanned ? "ScheduleDurationPlanned" : "ScheduleDurationAverage"].Value, Minutes(task.DurationMinutes));

    /// <summary>"1 h", "1 h 12", "24 min".</summary>
    public static string Minutes(int minutes)
    {
        var hours = minutes / 60;
        var rest = minutes % 60;
        if (hours == 0) return string.Create(CultureInfo.CurrentCulture, $"{rest} min");
        return rest == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{hours} h")
            : string.Create(CultureInfo.CurrentCulture, $"{hours} h {rest:D2}");
    }

    public static string Time(TimeOnly time) => time.ToString("t", CultureInfo.CurrentCulture);

    public static string DayName(int dayOfWeek) =>
        CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)(((dayOfWeek % 7) + 7) % 7));

    /// <summary>The short date and time of an instant, at the reader's clock.</summary>
    public static string DateTime(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(System.DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone)
            .ToString("g", CultureInfo.CurrentCulture);
}
