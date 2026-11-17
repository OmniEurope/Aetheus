// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Two runs of two different pipelines that share time, as the plan's occurrences say.</summary>
public sealed record ScheduleOverlapPair(ScheduledTaskDto First, ScheduledTaskDto Second, DateTime StartUtc, int Minutes);

/// <summary>
/// What the page's tiles and cards say about a plan, computed from the occurrences the server expanded and
/// the overlap minutes it filled: the counts, tonight's runs, the overlapping pairs and the next run. The
/// night is the reader's local 20:00 to 08:00. Pure: the clock and the zone come in as arguments.
/// </summary>
public sealed class SchedulePlanSummary
{
    private static readonly TimeOnly NightStart = new(20, 0);
    private static readonly TimeOnly NightEnd = new(8, 0);

    public int EnabledCount { get; private init; }
    public int DisabledCount { get; private init; }

    /// <summary>Runs that start between now and the end of the night in progress (or of the coming one).</summary>
    public int TonightRuns { get; private init; }

    public DateTime NightStartUtc { get; private init; }
    public DateTime NightEndUtc { get; private init; }
    public IReadOnlyList<ScheduleOverlapPair> Overlaps { get; private init; } = [];

    /// <summary>The enabled task whose next run comes first, with that instant (UTC).</summary>
    public (ScheduledTaskDto Task, DateTime AtUtc)? Next { get; private init; }

    /// <summary>The names of the pipelines each pipeline shares time with, by pipeline id.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> PartnersByPipeline { get; private init; } =
        new Dictionary<int, IReadOnlyList<string>>();

    public bool HasOverlap(int pipelineId) => PartnersByPipeline.ContainsKey(pipelineId);

    /// <summary>The summary of the plan. The server actions (recette R-8) count as runs of their own beside the
    /// pipelines, as <see cref="ServerActionPlannerTask"/> rows named by <paramref name="serverActionName"/> (the
    /// server's name when null); a switched-off task is a pipeline's, an action is always on. The CVE watch is drawn but
    /// is no run of the organization (no runner): it counts in no tile and no overlap.</summary>
    public static SchedulePlanSummary Build(
        SchedulePlanDto plan, TimeZoneInfo zone, DateTime nowUtc, Func<ScheduledServerActionDto, string>? serverActionName = null)
    {
        var (nightStart, nightEnd) = Night(zone, nowUtc);
        var enabled = plan.Tasks.Where(task => task.Enabled)
            .Concat(plan.ServerActions.Where(action => ServerActionDefaults.CountsInOverlaps(action.Kind)).Select(action => ServerActionPlannerTask.AsTask(action, serverActionName?.Invoke(action) ?? action.ServerName)))
            .ToList();
        var tonight = enabled
            .SelectMany(task => task.Occurrences)
            .Count(occurrence => occurrence.StartUtc >= Max(nightStart, nowUtc) && occurrence.StartUtc < nightEnd);
        var pairs = PairsOf(enabled, plan.ExecutionCapacity, nowUtc);
        var partners = new Dictionary<int, List<string>>();
        foreach (var pair in pairs)
        {
            Add(partners, pair.First, pair.Second.PipelineName);
            Add(partners, pair.Second, pair.First.PipelineName);
        }

        var next = enabled
            .Where(task => task.NextRunUtc is not null)
            .OrderBy(task => task.NextRunUtc)
            .FirstOrDefault();
        return new SchedulePlanSummary
        {
            EnabledCount = enabled.Count,
            DisabledCount = plan.Tasks.Count(task => !task.Enabled),
            TonightRuns = tonight,
            NightStartUtc = nightStart,
            NightEndUtc = nightEnd,
            Overlaps = pairs,
            Next = next is null ? null : (next, DateTime.SpecifyKind(next.NextRunUtc!.Value, DateTimeKind.Utc)),
            PartnersByPipeline = partners.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value)
        };
    }

    /// <summary>The night in progress, or the coming one, as UTC instants: the reader's 20:00 to 08:00.</summary>
    internal static (DateTime StartUtc, DateTime EndUtc) Night(TimeZoneInfo zone, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var evening = DateOnly.FromDateTime(local);
        // Before 08:00 the night in progress began the evening before.
        if (TimeOnly.FromDateTime(local) < NightEnd) evening = evening.AddDays(-1);
        var start = ToUtc(evening.ToDateTime(NightStart, DateTimeKind.Unspecified), zone);
        var end = ToUtc(evening.AddDays(1).ToDateTime(NightEnd, DateTimeKind.Unspecified), zone);
        return (start, end);
    }

    /// <summary>The pairs of runs of two tasks that share time still to come (what ended before <paramref name="nowUtc"/>
    /// is the past, the plan's window may reach back to the start of the week), counted only where more tasks run at
    /// once than the organization's <paramref name="capacity"/> allows (recette R-39).</summary>
    private static List<ScheduleOverlapPair> PairsOf(List<ScheduledTaskDto> tasks, int capacity, DateTime nowUtc)
    {
        var pairs = new List<ScheduleOverlapPair>();
        for (var first = 0; first < tasks.Count; first++)
        {
            for (var second = first + 1; second < tasks.Count; second++)
            {
                foreach (var a in tasks[first].Occurrences.Where(occurrence => occurrence.OverlapMinutes > 0))
                {
                    foreach (var b in tasks[second].Occurrences.Where(occurrence => occurrence.OverlapMinutes > 0))
                    {
                        var start = Max(a.StartUtc, b.StartUtc);
                        var end = a.EndUtc < b.EndUtc ? a.EndUtc : b.EndUtc;
                        if (start < end && end > nowUtc && (capacity <= 1 || ScheduleOverlapFinder.PeakConcurrency(tasks, start, end) > capacity))
                            pairs.Add(new ScheduleOverlapPair(tasks[first], tasks[second], start, (int)Math.Ceiling((end - start).TotalMinutes)));
                    }
                }
            }
        }

        return [.. pairs.OrderBy(pair => pair.StartUtc)];
    }

    private static void Add(Dictionary<int, List<string>> partners, ScheduledTaskDto task, string partner)
    {
        if (!partners.TryGetValue(task.PipelineId, out var list))
            partners[task.PipelineId] = list = [];
        if (!list.Contains(partner, StringComparer.Ordinal)) list.Add(partner);
    }

    private static DateTime ToUtc(DateTime wall, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(zone.IsInvalidTime(wall) ? wall.AddHours(1) : wall, zone);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
