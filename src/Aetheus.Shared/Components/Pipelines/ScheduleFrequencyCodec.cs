// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>A cron expression read as a named frequency. <see cref="Hour"/> and <see cref="Minute"/>
/// are set whenever the expression is a single time of day at second zero, also for
/// <see cref="ScheduleFrequency.Custom"/>, so the block stays movable in time.</summary>
public sealed record ScheduleFrequencySpec(ScheduleFrequency Frequency, int? Day, int? Hour, int? Minute);

/// <summary>
/// Pure conversion between the six-field cron expression of a pipeline (seconds first) and the
/// planner's vocabulary. Only the exact shapes the planner writes are named; anything else (lists,
/// intervals, ranges other than 1-5, non-zero seconds, a month field) is
/// <see cref="ScheduleFrequency.Custom"/>, never approximated to a named frequency.
/// </summary>
public static class ScheduleFrequencyCodec
{
    public const int MaxMonthDay = 28;
    private const string EveryTwoDaysDayField = "1-31/2";
    private const string WeekdaysDayOfWeekField = "1-5";

    public static ScheduleFrequencySpec Decode(string? cron)
    {
        var fields = (cron ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 6)
        {
            return Custom(null, null);
        }

        var (second, dom, month, dow) = (fields[0], fields[3], fields[4], fields[5]);
        var minute = ParseNumber(fields[1], 59);
        var hour = ParseNumber(fields[2], 23);
        if (second != "0" || minute is null || hour is null)
        {
            return Custom(null, null);
        }

        return month != "*" ? Custom(hour, minute) : DecodeDays(dom, dow, hour.Value, minute.Value);
    }

    private static ScheduleFrequencySpec DecodeDays(string dom, string dow, int hour, int minute)
    {
        var fixedShape = (dom, dow) switch
        {
            ("*", "*") => ScheduleFrequency.EveryEvening,
            ("*", WeekdaysDayOfWeekField) => ScheduleFrequency.Weekdays,
            (EveryTwoDaysDayField, "*") => ScheduleFrequency.EveryTwoDays,
            _ => ScheduleFrequency.Custom
        };

        return fixedShape != ScheduleFrequency.Custom
            ? new(fixedShape, null, hour, minute)
            : DecodeNumberedDay(dom, dow, hour, minute);
    }

    private static ScheduleFrequencySpec DecodeNumberedDay(string dom, string dow, int hour, int minute)
    {
        if (dom == "*" && ParseNumber(dow, 6) is { } weekday)
        {
            return new(ScheduleFrequency.Weekly, weekday, hour, minute);
        }

        if (dow == "*" && ParseNumber(dom, MaxMonthDay) is { } monthDay and >= 1)
        {
            return new(ScheduleFrequency.Monthly, monthDay, hour, minute);
        }

        return Custom(hour, minute);
    }

    /// <summary>The cron expression for a named frequency, or null when the spec cannot be written:
    /// <see cref="ScheduleFrequency.Custom"/>, a time out of range, or a missing or out-of-range day.</summary>
    public static string? Encode(ScheduleFrequencySpec spec)
    {
        if (spec.Hour is not (>= 0 and <= 23) || spec.Minute is not (>= 0 and <= 59))
        {
            return null;
        }

        var time = $"0 {spec.Minute} {spec.Hour}";
        return spec.Frequency switch
        {
            ScheduleFrequency.EveryEvening => $"{time} * * *",
            ScheduleFrequency.Weekdays => $"{time} * * {WeekdaysDayOfWeekField}",
            ScheduleFrequency.EveryTwoDays => $"{time} {EveryTwoDaysDayField} * *",
            ScheduleFrequency.Weekly when spec.Day is >= 0 and <= 6 => $"{time} * * {spec.Day}",
            ScheduleFrequency.Monthly when spec.Day is >= 1 and <= MaxMonthDay => $"{time} {spec.Day} * *",
            _ => null
        };
    }

    private static ScheduleFrequencySpec Custom(int? hour, int? minute) => new(ScheduleFrequency.Custom, null, hour, minute);

    private static int? ParseNumber(string field, int max)
    {
        if (field.Length is 0 or > 2 || !field.All(char.IsAsciiDigit))
        {
            return null;
        }

        var value = int.Parse(field, CultureInfo.InvariantCulture);
        return value <= max ? value : null;
    }
}
