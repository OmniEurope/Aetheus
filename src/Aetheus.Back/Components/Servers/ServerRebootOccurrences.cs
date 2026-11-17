// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;
using Cronos;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// The UTC instants of a daily reboot kept at a wall-clock time of its time zone (recette R-8). Cronos resolves the
/// daylight-saving edges: a time skipped by the spring change runs at the first instant after it, a time repeated by
/// the autumn change runs once.
/// </summary>
internal static class ServerRebootOccurrences
{
    /// <summary>The zone of an identifier, or null when the host does not know it (the request is then refused).</summary>
    public static TimeZoneInfo? FindZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return null;
        }

        if (string.Equals(timeZoneId, ServerRebootOrder.UtcTimeZone, StringComparison.Ordinal))
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone) ? zone : null;
    }

    /// <summary>The occurrences in <c>(fromUtc, toUtc]</c>, oldest first.</summary>
    public static List<DateTime> Between(ServerRebootSchedule schedule, DateTime fromUtc, DateTime toUtc)
    {
        var zone = FindZone(schedule.TimeZoneId);
        if (zone is null || toUtc <= fromUtc)
        {
            return [];
        }

        return [.. Cron(schedule).GetOccurrences(Utc(fromUtc), Utc(toUtc), zone, fromInclusive: false, toInclusive: true)];
    }

    /// <summary>The first occurrence strictly after <paramref name="afterUtc"/>.</summary>
    public static DateTime? Next(ServerRebootSchedule schedule, DateTime afterUtc)
    {
        var zone = FindZone(schedule.TimeZoneId);
        return zone is null ? null : Cron(schedule).GetNextOccurrence(Utc(afterUtc), zone, inclusive: false);
    }

    /// <summary>The five-field expression of the daily time, as the planner's codec reads it.</summary>
    public static string ToCronExpression(int hour, int minute) =>
        string.Create(CultureInfo.InvariantCulture, $"{minute} {hour} * * *");

    private static CronExpression Cron(ServerRebootSchedule schedule) =>
        CronExpression.Parse(ToCronExpression(schedule.Hour, schedule.Minute));

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
