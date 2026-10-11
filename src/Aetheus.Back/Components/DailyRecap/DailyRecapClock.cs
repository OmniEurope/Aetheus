// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// The calendar arithmetic of the recap, kept pure so it can be tested across timezones and DST
/// changes: which local day it is, the UTC bounds of a local day, and whether a send is due.
/// </summary>
internal static class DailyRecapClock
{
    public static bool TryResolveTimeZone(string? id, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out timeZone!);
    }

    public static DateOnly LocalToday(DateTime utcNow, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utcNow), timeZone));

    /// <summary>
    /// The UTC instants where the local day starts and the next one starts. A day around a DST change
    /// lasts 23 or 25 hours; a midnight that the change skips starts the day at the first valid minute.
    /// </summary>
    public static (DateTime StartUtc, DateTime EndUtc) DayWindowUtc(DateOnly day, TimeZoneInfo timeZone) =>
        (LocalMidnightToUtc(day, timeZone), LocalMidnightToUtc(day.AddDays(1), timeZone));

    public static DailyRecapScheduleDecision Decide(
        DateTime utcNow, bool enabled, TimeOnly sendTime, TimeZoneInfo timeZone, DateOnly? lastSentDate)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utcNow), timeZone);
        var today = DateOnly.FromDateTime(local);
        if (!enabled) return new(DailyRecapScheduleOutcome.Disabled, today);
        if (lastSentDate is { } last && last >= today) return new(DailyRecapScheduleOutcome.AlreadySent, today);
        // A send time already passed (the backend was down at 07:00) is still due today, once.
        return TimeOnly.FromDateTime(local) < sendTime
            ? new(DailyRecapScheduleOutcome.NotYet, today)
            : new(DailyRecapScheduleOutcome.Due, today);
    }

    private static DateTime LocalMidnightToUtc(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // A zone whose clocks jump at midnight has no 00:00 that day: the day begins at the first minute that exists.
        while (timeZone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
