// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Pipelines;
using Cronos;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The occurrences kept for one task and one window. <see cref="IsTruncated"/> says the
/// expression still fires after the last one kept: the rest of the window is not free.</summary>
public sealed record ScheduleExpansion(List<ScheduleOccurrenceDto> Occurrences, bool IsTruncated);

/// <summary>
/// Expands a pipeline cron expression into its occurrences over a UTC window, parsed exactly as
/// <c>PipelineSchedulerService</c> does (six fields, seconds first, <see cref="CronFormat.IncludeSeconds"/>,
/// evaluated in UTC). The window is half-open: <c>[from, to)</c>.
/// </summary>
public static class ScheduleOccurrenceExpander
{
    /// <summary>Upper bound of occurrences returned for one task and one window, so an every-second
    /// expression cannot produce millions of items. An every-minute cron over a week (10 080) is cut
    /// at this bound; the page never needs more.</summary>
    public const int MaxOccurrences = 1000;

    /// <summary>The occurrences of <paramref name="cronExpression"/> in <c>[fromUtc, toUtc)</c>, each
    /// lasting <paramref name="durationMinutes"/>, at most <see cref="MaxOccurrences"/> (the earliest
    /// ones). An empty, unparsable or inverted window gives an empty list; never throws.</summary>
    public static List<ScheduleOccurrenceDto> Expand(string? cronExpression, DateTime fromUtc, DateTime toUtc, int durationMinutes) =>
        ExpandBounded(cronExpression, fromUtc, toUtc, durationMinutes).Occurrences;

    /// <summary>Same as <see cref="Expand"/>, and says whether the bound cut the list: an expression
    /// with exactly <see cref="MaxOccurrences"/> occurrences in the window is complete, not truncated.</summary>
    public static ScheduleExpansion ExpandBounded(string? cronExpression, DateTime fromUtc, DateTime toUtc, int durationMinutes)
    {
        var result = new List<ScheduleOccurrenceDto>();
        var from = AsUtc(fromUtc);
        var to = AsUtc(toUtc);
        if (from >= to || Parse(cronExpression) is not { } cron)
        {
            return new ScheduleExpansion(result, false);
        }

        var duration = TimeSpan.FromMinutes(Math.Max(durationMinutes, 0));
        foreach (var start in cron.GetOccurrences(from, to, TimeZoneInfo.Utc, fromInclusive: true, toInclusive: false))
        {
            if (result.Count >= MaxOccurrences)
            {
                return new ScheduleExpansion(result, true);
            }

            result.Add(new ScheduleOccurrenceDto { StartUtc = start, EndUtc = start + duration });
        }

        return new ScheduleExpansion(result, false);
    }

    /// <summary>The first occurrence strictly after <paramref name="afterUtc"/>, whatever the window;
    /// null for an unparsable expression or one that never fires again. Never throws.</summary>
    public static DateTime? NextOccurrence(string? cronExpression, DateTime afterUtc) =>
        Parse(cronExpression)?.GetNextOccurrence(AsUtc(afterUtc), TimeZoneInfo.Utc);

    /// <summary>True when the engine parses <paramref name="cronExpression"/> (six fields, seconds first):
    /// the scheduler skips, with a warning, a pipeline whose expression is not.</summary>
    public static bool IsFiredByTheEngine(string? cronExpression) => Parse(cronExpression) is not null;

    /// <summary>The instant in UTC; a value with no kind is read as UTC, as the engine reads its cron.</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static CronExpression? Parse(string? cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            return null;
        }

        try
        {
            return CronExpression.Parse(cronExpression.Trim(), CronFormat.IncludeSeconds);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }
}
