// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>One step under an expanded stage row, on the run's own time axis.</summary> <param
/// name="Step">The step this row describes.</param> <param name="Label">The step name, followed by
/// its matrix leg when it has one, so the legs of one matrix step can be told apart.</param> <param
/// name="HasBar">False for a step with no recorded start: it is listed with its status and gets no
/// bar, since it occupied no time the run can show.</param> <param name="OffsetPercent">Distance
/// from the left edge of the axis, 0 to 100.</param> <param name="WidthPercent">Bar width, never
/// below <see cref="RunGanttChart.MinimumBarWidthPercent"/> for a step that started.</param> <param
/// name="IsRunning">Started but not finished: its right edge is "now".</param> <param
/// name="Duration">Start to end (or to now while running); zero without a start.</param>
internal sealed record RunGanttStepBar(
    PipelineStepRunDto Step,
    string Label,
    bool HasBar,
    double OffsetPercent,
    double WidthPercent,
    bool IsRunning,
    TimeSpan Duration);

/// <summary> Places a stage's steps on the SAME axis as the stage bars, so an expanded stage reads
/// as its own bar broken down into what ran inside it. Pure, like <see cref="RunGanttChart"/>: the
/// clock is a parameter and nothing is rendered here. </summary>
internal static class RunGanttStepChart
{
    /// <summary>Lays out <paramref name="stage"/>'s steps, in the order the stage holds them.</summary>
    /// <param name="stage">The stage whose steps are expanded.</param>
    /// <param name="layout">The run's layout, whose start and duration define the shared axis.</param>
    /// <param name="now">The right edge of a step still running. A running step is never drawn past
    /// the axis end: the axis already reaches "now" whenever its stage is running, and when the stage
    /// claims to be finished the step cannot honestly extend beyond the chart.</param>
    public static IReadOnlyList<RunGanttStepBar> Build(StageViewModel stage, RunGanttLayout layout, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(layout);

        var axisStart = layout.Start;
        var axisEnd = layout.Start + layout.Duration;
        var totalSeconds = layout.Duration.TotalSeconds;
        var degenerate = totalSeconds <= 0;

        var bars = new List<RunGanttStepBar>(stage.Steps.Count);
        foreach (var step in stage.Steps)
        {
            var label = LabelOf(step);
            if (step.StartedAt is not { } start)
            {
                bars.Add(new RunGanttStepBar(step, label, false, 0, 0, false, TimeSpan.Zero));
                continue;
            }

            var isRunning = !step.CompletedAt.HasValue;
            var end = step.CompletedAt ?? now;
            // Same clock-skew clamp as the stage bars: an end before its start is a brief bar, not
            // a negative width that renders as nothing.
            if (end < start) end = start;
            var duration = end - start;

            var drawStart = start < axisStart ? axisStart : start;
            var drawEnd = end > axisEnd ? axisEnd : end;
            if (drawEnd < drawStart) drawEnd = drawStart;

            var offset = degenerate ? 0 : (drawStart - axisStart).TotalSeconds / totalSeconds * 100;
            var width = degenerate ? 100 : (drawEnd - drawStart).TotalSeconds / totalSeconds * 100;
            offset = Math.Clamp(offset, 0, 100);
            width = Math.Max(width, RunGanttChart.MinimumBarWidthPercent);
            if (offset + width > 100) offset = Math.Max(0, 100 - width);

            bars.Add(new RunGanttStepBar(step, label, true, offset, width, isRunning, duration));
        }

        return bars;
    }

    /// <summary>The step name, plus its matrix leg in parentheses when present.</summary>
    internal static string LabelOf(PipelineStepRunDto step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var leg = step.MatrixLeg?.Trim();
        return string.IsNullOrEmpty(leg) ? step.StepName : $"{step.StepName} ({leg})";
    }
}
