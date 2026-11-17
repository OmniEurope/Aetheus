// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;
using Cronos;

namespace Aetheus.Back.Components.Servers;

/// <summary>The times of one read-only planner row of a machine's own schedule: the starts inside the window, the next one and
/// whether the window held more than the bound.</summary>
internal sealed record MachineScheduleTimes(List<DateTime> Starts, DateTime? Next, bool Truncated);

/// <summary>How the times of a machine's own schedule (the reboots of R-11, the jobs of R-27), as its agent read them, become
/// planner times. A time is shown only when it was read: an expression the engine rejects or a zone this host does not know
/// gives nothing, never a guess.</summary>
internal static partial class MachineSchedule
{
    // Same bound as the other planner sources (ScheduleOccurrenceExpander.MaxOccurrences, a module above this one).
    private const int MaxOccurrences = 1000;

    [GeneratedRegex(@"^(\d{1,2}) (\d{1,2}) \* \* \*$")]
    private static partial Regex DailyPattern();

    /// <summary>The single instant of something already scheduled on the host.</summary>
    public static MachineScheduleTimes Instant(DateTime at, DateTime fromUtc, DateTime toUtc, DateTime now)
    {
        var instant = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        List<DateTime> inWindow = instant >= fromUtc && instant < toUtc ? [instant] : [];
        return new MachineScheduleTimes(inWindow, instant > now ? instant : null, false);
    }

    /// <summary>The occurrences of a five-field expression read in <paramref name="zoneId"/>; null when the expression is
    /// empty or unreadable by the engine, or the zone is unknown to this host.</summary>
    public static MachineScheduleTimes? Expression(string? cronExpression, string? zoneId, DateTime fromUtc, DateTime toUtc, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(cronExpression)
            || ServerRebootOccurrences.FindZone(zoneId) is not { } zone
            || !CronExpression.TryParse(cronExpression.Trim(), CronFormat.Standard, out var cron))
        {
            return null;
        }

        var all = cron.GetOccurrences(fromUtc, toUtc, zone, fromInclusive: true, toInclusive: false)
            .Take(MaxOccurrences + 1)
            .ToList();
        var truncated = all.Count > MaxOccurrences;
        return new MachineScheduleTimes(
            [.. all.Take(MaxOccurrences)], cron.GetNextOccurrence(now, zone, inclusive: false), truncated);
    }

    /// <summary><c>HH:mm</c> of an expression that fires once a day (<c>30 3 * * *</c>), else null.</summary>
    public static string? DailyTime(string? cron)
    {
        if (cron is null || DailyPattern().Match(cron) is not { Success: true } time) return null;
        // Minutes first, as cron writes it.
        var minute = int.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture);
        var hour = int.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture);
        return hour > 23 || minute > 59 ? null : string.Create(CultureInfo.InvariantCulture, $"{hour:00}:{minute:00}");
    }
}
