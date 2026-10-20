// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>One stage's bar: where it starts and how wide it is, both as percentages of the
/// run.</summary> <param name="Stage">The stage this bar describes.</param> <param
/// name="OffsetPercent">Distance from the left edge, 0 to 100.</param> <param
/// name="WidthPercent">Bar width, never zero for a stage that actually ran.</param> <param
/// name="IsRunning">Still running, so its right edge is "now" rather than a recorded end.</param>
/// <param name="Wait">how long the stage waited, from the end of what it depended on to its own
/// start. Zero when it started at once (or when clock skew puts that end after the start).</param>
/// <param name="WaitOffsetPercent">Left edge of the wait segment, 0 to 100.</param> <param
/// name="WaitWidthPercent">Width of the wait segment. It always ends exactly where the bar begins,
/// so the two read as one continuous line: waiting, then running.</param>
internal sealed record RunGanttBar(
    StageViewModel Stage,
    double OffsetPercent,
    double WidthPercent,
    bool IsRunning,
    TimeSpan Wait = default,
    double WaitOffsetPercent = 0,
    double WaitWidthPercent = 0)
{
    public bool HasWait => WaitWidthPercent > 0;
}

/// <summary>The bars of one run plus the span they are measured against.</summary>
/// <param name="Bars">Bars in the order the stages were given, so the chart lines up with the list
/// beside it.</param>
/// <param name="Start">The instant the run began, which is the chart's left edge.</param>
/// <param name="Duration">The span the chart covers.</param>
/// <param name="StagesWithoutTiming">Stages that never started (skipped, or still pending). They get
/// no bar: drawing one would put a shape on the time axis for something that occupied no time.</param>
internal sealed record RunGanttLayout(
    IReadOnlyList<RunGanttBar> Bars,
    DateTime Start,
    TimeSpan Duration,
    IReadOnlyList<StageViewModel> StagesWithoutTiming)
{
    public bool HasBars => Bars.Count > 0;
}

/// <summary>
/// Places each stage on a time axis, which is the one thing the ordered list beside it cannot show:
/// where the run actually spent its time, and which stages genuinely overlapped.
///
/// The list can say "runs in parallel" because two stages share a graph depth. That is what the
/// definition allows, not what happened: two stages at the same depth run one after the other when a
/// single runner serves both, and the list looks identical either way. Bars on a shared axis make the
/// difference visible.
///
/// Pure on purpose, so every rule below is tested without rendering anything.
/// </summary>
internal static class RunGanttChart
{
    /// <summary>A bar narrower than this is invisible, so a stage that took a fraction of the run
    /// would silently vanish from the chart. Short stages are drawn at this width and read as
    /// "brief", which is true, rather than as absent, which is not.</summary>
    internal const double MinimumBarWidthPercent = 0.6;

    /// <summary>
    /// Builds the layout, or null when the run has nothing to place on a time axis.
    /// </summary>
    /// <param name="stages">The run's stages, in display order.</param>
    /// <param name="now">The right edge for stages still running. Passed in rather than read from the
    /// clock so the layout stays a pure function.</param>
    public static RunGanttLayout? Build(IReadOnlyList<StageViewModel> stages, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var timed = stages.Where(stage => stage.StartedAt.HasValue).ToList();
        var untimed = stages.Where(stage => !stage.StartedAt.HasValue).ToList();
        if (timed.Count == 0) return null;

        var start = timed.Min(stage => stage.StartedAt!.Value);
        // A stage still running has no recorded end; it reaches "now". Taking Max over ends alone
        // would stop the axis at the last COMPLETED stage, and a running stage would then overflow
        // the chart or be clipped out of it.
        var end = timed
            .Select(stage => stage.CompletedAt ?? now)
            .Append(start)
            .Max();

        var duration = end - start;
        // Every stage started in the same instant the axis begins: a run that is one second old, or
        // a fixture with identical timestamps. There is no scale to divide by, so give each bar the
        // full width rather than dividing by zero.
        var totalSeconds = duration.TotalSeconds;
        var degenerate = totalSeconds <= 0;

        var bars = new List<RunGanttBar>(timed.Count);
        foreach (var stage in timed)
        {
            var stageStart = stage.StartedAt!.Value;
            var isRunning = !stage.CompletedAt.HasValue;
            var stageEnd = stage.CompletedAt ?? now;
            // Clock skew between the control plane and a runner can put an end before its own start.
            // Clamping keeps such a stage on the chart as a brief bar instead of a negative width
            // that renders as nothing at all.
            if (stageEnd < stageStart) stageEnd = stageStart;

            var offset = degenerate ? 0 : (stageStart - start).TotalSeconds / totalSeconds * 100;
            var width = degenerate ? 100 : (stageEnd - stageStart).TotalSeconds / totalSeconds * 100;

            offset = Math.Clamp(offset, 0, 100);
            width = Math.Max(width, MinimumBarWidthPercent);
            // A widened short bar at the far right would otherwise run past the axis.
            if (offset + width > 100) offset = Math.Max(0, 100 - width);

            // The wait is drawn on real time, from the reference end to this start. The bars
            // themselves never move; the segment only fills the gap before each one.
            var waitStart = WaitReference(stage, stages, start);
            var wait = stageStart > waitStart ? stageStart - waitStart : TimeSpan.Zero;
            var waitStartOffset = degenerate ? 0 : Math.Clamp((waitStart - start).TotalSeconds / totalSeconds * 100, 0, 100);
            // Ends where the bar begins, even when a short bar was nudged left to stay on the axis.
            var waitWidth = wait > TimeSpan.Zero ? Math.Max(0, offset - waitStartOffset) : 0;

            bars.Add(new RunGanttBar(stage, offset, width, isRunning, wait, offset - waitWidth, waitWidth));
        }

        return new RunGanttLayout(bars, start, duration, untimed);
    }

    /// <summary>A stage becomes runnable at the latest predecessor end, falling back to the latest
    /// completed stage or run start when dependency information is absent. Clock skew is clamped to
    /// a zero wait.</summary>
    internal static DateTime WaitReference(StageViewModel stage, IReadOnlyList<StageViewModel> stages, DateTime runStart)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(stages);
        var stageStart = stage.StartedAt ?? runStart;

        if (stage.Depth is { } depth)
        {
            var dependencyEnd = stages
                .Where(other => other.Depth < depth && other.CompletedAt.HasValue)
                .Select(other => (DateTime?)other.CompletedAt!.Value)
                .Max();
            if (dependencyEnd is { } end) return end;
        }

        // "Previous" in time, not in the list: the list puts stages without a depth after the graph,
        // so the clone that runs first would otherwise have nothing before it.
        var previousEnd = stages
            .Where(other => !ReferenceEquals(other, stage)
                            && other.CompletedAt is { } end && end <= stageStart)
            .Select(other => (DateTime?)other.CompletedAt!.Value)
            .Max();

        return previousEnd is { } previous && previous > runStart ? previous : runStart;
    }

    /// <summary> Where the run's time went, summed over the stages. Execution plus wait equals the
    /// span of the axis when stages ran one after the other; stages that overlapped count their
    /// time once each, so the execution sum can then exceed the span. </summary>
    public static RunGanttTotals Totals(RunGanttLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var end = layout.Start + layout.Duration;
        var execution = TimeSpan.Zero;
        var wait = TimeSpan.Zero;
        foreach (var bar in layout.Bars)
        {
            var stageStart = bar.Stage.StartedAt!.Value;
            var stageEnd = bar.Stage.CompletedAt ?? end;
            if (stageEnd > stageStart) execution += stageEnd - stageStart;
            wait += bar.Wait;
        }

        return new RunGanttTotals(execution, wait);
    }

    /// <summary>
    /// Whether these two stages actually overlapped in time. Unlike the graph-depth hint the list
    /// shows, this answers what the run did rather than what its definition permitted.
    /// </summary>
    public static bool Overlaps(RunGanttBar first, RunGanttBar second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.OffsetPercent < second.OffsetPercent + second.WidthPercent
            && second.OffsetPercent < first.OffsetPercent + first.WidthPercent;
    }
}

/// <summary>Cumulative execution and cumulative wait over a run's stages.</summary>
internal sealed record RunGanttTotals(TimeSpan Execution, TimeSpan Wait);

/// <summary>Running-stage progress compares elapsed time with its usual duration.</summary> <param
/// name="FillPercent">Elapsed time as a percentage of the average, capped at 100.</param> <param
/// name="OverrunPercent">Time beyond the average as a percentage of the average, capped at 100; the
/// duration column shows further overrun.</param>
internal sealed record RunGanttProgress(double FillPercent, double OverrunPercent)
{
    public bool IsOverrunning => OverrunPercent > 0;

    /// <summary>Null when there is no usual duration to compare against (no successful run yet, or an
    /// average of zero), so the bar is not filled with a share of nothing.</summary>
    public static RunGanttProgress? Of(TimeSpan elapsed, double? averageSeconds)
    {
        if (averageSeconds is not > 0) return null;
        var ratio = Math.Max(0, elapsed.TotalSeconds) / averageSeconds.Value;
        return new RunGanttProgress(
            Math.Min(ratio, 1) * 100,
            Math.Clamp(ratio - 1, 0, 1) * 100);
    }
}
