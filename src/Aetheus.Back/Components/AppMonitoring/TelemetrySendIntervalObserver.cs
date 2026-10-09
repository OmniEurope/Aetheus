// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// Derives the interval an application actually sends its metrics at from the instants of its samples.
/// The points of one send share their instant (a periodic reader stamps them at collection), so the
/// instants within <see cref="SameSendTolerance"/> of each other are one send. The interval is the gap
/// that recurs among the last <see cref="RecentGaps"/> gaps between sends, each rounded to
/// <see cref="GapRounding"/>: a recurring gap rather than the shortest or the mean, because a stopped
/// application leaves one long gap and an application whose metrics are only exported when a route is
/// called leaves gaps of several intervals. Without a gap seen at least <see cref="MinimumOccurrences"/>
/// times, nothing is claimed.
/// </summary>
public static class TelemetrySendIntervalObserver
{
    internal static readonly TimeSpan SameSendTolerance = TimeSpan.FromSeconds(2);
    internal const int RecentGaps = 6;
    internal const int MinimumOccurrences = 3;
    internal const int GapRounding = 5;

    /// <summary>The observed interval in seconds, or null when the instants do not show one.</summary>
    public static int? ObserveSeconds(IEnumerable<DateTime> instants)
    {
        ArgumentNullException.ThrowIfNull(instants);
        var sends = new List<DateTime>();
        foreach (var instant in instants.OrderByDescending(value => value))
        {
            if (sends.Count == 0 || sends[^1] - instant > SameSendTolerance)
                sends.Add(instant);
            if (sends.Count > RecentGaps)
                break;
        }

        var gaps = new List<int>();
        for (var index = 1; index < sends.Count; index++)
        {
            var seconds = (sends[index - 1] - sends[index]).TotalSeconds;
            gaps.Add((int)(Math.Round(seconds / GapRounding, MidpointRounding.AwayFromZero) * GapRounding));
        }

        // Gaps are newest first, so on a tie the newest wins: an interval that just changed shows as soon
        // as it has recurred as often as the old one.
        var recurring = gaps
            .Select((gap, index) => (Gap: gap, Index: index))
            .Where(entry => entry.Gap > 0)
            .GroupBy(entry => entry.Gap, entry => entry.Index)
            .Where(group => group.Count() >= MinimumOccurrences)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Min())
            .FirstOrDefault();
        return recurring?.Key;
    }
}
