// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary>
/// Where a run actually spent its time. The ordered list beside this chart can only say two stages
/// were ALLOWED to run in parallel, because they share a graph depth; whether they really did depends
/// on how many runners were free, and the list looks the same either way.
///
/// So the cases that matter here are the ones where a bar would lie: a stage that never started, one
/// still running, one shorter than a pixel, and a run so short there is no scale at all.
/// </summary>
public sealed class RunGanttChartTests
{
    private static readonly DateTime Origin = new(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);

    private static StageViewModel Stage(
        string name, int? startSeconds, int? endSeconds,
        TaskExecutionStatus status = TaskExecutionStatus.Success, int? depth = null) => new()
        {
            Name = name,
            DisplayName = name,
            Status = status,
            StartedAt = startSeconds is { } s ? Origin.AddSeconds(s) : null,
            CompletedAt = endSeconds is { } e ? Origin.AddSeconds(e) : null,
            Depth = depth
        };

    private static RunGanttLayout Build(params StageViewModel[] stages) =>
        RunGanttChart.Build(stages, Origin.AddSeconds(1000))!;

    [Fact]
    public void BarsArePlacedAndScaledAgainstTheWholeRun()
    {
        var layout = Build(
            Stage("Compile", 0, 100),
            Stage("Test", 100, 200));

        Assert.Equal(TimeSpan.FromSeconds(200), layout.Duration);
        Assert.Equal(Origin, layout.Start);
        Assert.Equal(0, layout.Bars[0].OffsetPercent, 3);
        Assert.Equal(50, layout.Bars[0].WidthPercent, 3);
        Assert.Equal(50, layout.Bars[1].OffsetPercent, 3);
        Assert.Equal(50, layout.Bars[1].WidthPercent, 3);
    }

    [Fact]
    public void TheChartShowsWhetherTwoStagesReallyOverlapped()
    {
        // The whole reason for the chart: same graph depth, but one ran after the other because a
        // single runner served both. The list shows "in parallel" for both cases.
        var sequential = Build(Stage("A", 0, 100), Stage("B", 100, 200));
        Assert.False(RunGanttChart.Overlaps(sequential.Bars[0], sequential.Bars[1]));

        var concurrent = Build(Stage("A", 0, 100), Stage("B", 20, 120));
        Assert.True(RunGanttChart.Overlaps(concurrent.Bars[0], concurrent.Bars[1]));
    }

    [Fact]
    public void AStageThatNeverStartedGetsNoBar()
    {
        // A skipped or pending stage occupied no time; a bar for it would claim otherwise.
        var layout = Build(
            Stage("Compile", 0, 100),
            Stage("Deploy", null, null, TaskExecutionStatus.Pending));

        Assert.Single(layout.Bars);
        Assert.Equal("Deploy", Assert.Single(layout.StagesWithoutTiming).DisplayName);
    }

    [Fact]
    public void ARunWhereNothingStartedHasNoChartAtAll()
    {
        Assert.Null(RunGanttChart.Build(
            [Stage("Waiting", null, null, TaskExecutionStatus.Pending)], Origin));
    }

    [Fact]
    public void ARunningStageReachesNowRatherThanStoppingAtTheLastCompletedOne()
    {
        // Measured to `now`, not to the last recorded end: otherwise the axis would stop at the
        // finished stage and the running one would be clipped off the chart.
        var layout = RunGanttChart.Build(
            [Stage("Compile", 0, 100), Stage("Test", 100, null, TaskExecutionStatus.Running)],
            Origin.AddSeconds(300));

        Assert.Equal(TimeSpan.FromSeconds(300), layout!.Duration);
        Assert.True(layout.Bars[1].IsRunning);
        Assert.Equal(100.0 / 3, layout.Bars[1].OffsetPercent, 3);
        Assert.Equal(200.0 / 3, layout.Bars[1].WidthPercent, 3);
    }

    [Fact]
    public void AVeryShortStageStaysVisibleInsteadOfDisappearing()
    {
        var layout = Build(Stage("Blink", 0, 1), Stage("Long", 1, 1000));

        Assert.Equal(RunGanttChart.MinimumBarWidthPercent, layout.Bars[0].WidthPercent, 3);
    }

    [Fact]
    public void AShortStageAtTheVeryEndStaysInsideTheAxis()
    {
        var layout = Build(Stage("Long", 0, 999), Stage("Blink", 999, 1000));

        var last = layout.Bars[1];
        Assert.True(
            last.OffsetPercent + last.WidthPercent <= 100.0001,
            $"bar ends at {last.OffsetPercent + last.WidthPercent}%");
    }

    [Fact]
    public void ARunWithNoElapsedTimeGivesEveryStageAFullBarRatherThanDividingByZero()
    {
        var layout = Build(Stage("A", 0, 0), Stage("B", 0, 0));

        Assert.All(layout.Bars, bar =>
        {
            Assert.Equal(0, bar.OffsetPercent, 3);
            Assert.Equal(100, bar.WidthPercent, 3);
        });
    }

    [Fact]
    public void AnEndBeforeItsOwnStartIsClampedRatherThanDrawnBackwards()
    {
        // Clock skew between the control plane and a runner. A negative width renders as nothing,
        // which would drop the stage from the chart without saying so.
        var layout = Build(Stage("Skewed", 100, 50), Stage("Normal", 0, 200));

        Assert.True(layout.Bars[0].WidthPercent > 0);
        Assert.True(layout.Bars[0].OffsetPercent >= 0);
    }

    [Fact]
    public void BarsKeepTheOrderTheStagesWereGivenIn()
    {
        // The chart sits beside the ordered list; reordering here would break the correspondence.
        var layout = Build(Stage("Third", 200, 300), Stage("First", 0, 100), Stage("Second", 100, 200));

        Assert.Equal(
            ["Third", "First", "Second"],
            layout.Bars.Select(bar => bar.Stage.DisplayName));
    }

    // The wait before each stage, drawn on real time, glued to the left of its bar.

    [Fact]
    public void ASequentialStageWaitsFromTheEndOfTheStageItDependsOn()
    {
        // Prepare (no depth), then Build at depth 0, then Deploy at depth 1 which started 30 s after
        // Build ended: the 30 s are the wait, and the bars stay where the clock put them.
        var layout = Build(
            Stage("Prepare", 0, 10),
            Stage("Build", 20, 100, depth: 0),
            Stage("Deploy", 130, 200, depth: 1));

        Assert.Equal(TimeSpan.Zero, layout.Bars[0].Wait);
        Assert.Equal(TimeSpan.FromSeconds(10), layout.Bars[1].Wait);
        Assert.Equal(TimeSpan.FromSeconds(30), layout.Bars[2].Wait);
        Assert.Equal(65, layout.Bars[2].OffsetPercent, 3);
        Assert.Equal(50, layout.Bars[2].WaitOffsetPercent, 3);
        Assert.Equal(15, layout.Bars[2].WaitWidthPercent, 3);
        Assert.Equal(layout.Bars[2].OffsetPercent,
            layout.Bars[2].WaitOffsetPercent + layout.Bars[2].WaitWidthPercent, 6);
    }

    [Fact]
    public void AStageWaitsForTheLatestOfTheStagesAtALowerDepth()
    {
        // Two parallel stages at depth 0 end at 50 and 80; the depth-1 stage could only start after
        // both, so its wait runs from 80, not from the first one to finish.
        var layout = Build(
            Stage("Lint", 0, 50, depth: 0),
            Stage("Test", 0, 80, depth: 0),
            Stage("Package", 90, 100, depth: 1));

        Assert.Equal(TimeSpan.Zero, layout.Bars[0].Wait);
        Assert.Equal(TimeSpan.Zero, layout.Bars[1].Wait);
        Assert.Equal(TimeSpan.FromSeconds(10), layout.Bars[2].Wait);
        Assert.Equal(80, layout.Bars[2].WaitOffsetPercent, 3);
    }

    [Fact]
    public void ADependencyEndingAfterTheStageStartedGivesNoWaitRatherThanANegativeOne()
    {
        // Clock skew: the runner reports the dependency's end 5 s after the next stage started.
        var layout = Build(
            Stage("Build", 0, 105, depth: 0),
            Stage("Deploy", 100, 200, depth: 1));

        Assert.Equal(TimeSpan.Zero, layout.Bars[1].Wait);
        Assert.False(layout.Bars[1].HasWait);
        Assert.Equal(0, layout.Bars[1].WaitWidthPercent, 6);
    }

    [Fact]
    public void ARunningStageStillShowsTheWaitBeforeIt()
    {
        var layout = RunGanttChart.Build(
            [Stage("Build", 0, 100, depth: 0), Stage("Test", 150, null, TaskExecutionStatus.Running, depth: 1)],
            Origin.AddSeconds(300))!;

        Assert.True(layout.Bars[1].IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(50), layout.Bars[1].Wait);
        Assert.Equal(100.0 / 3, layout.Bars[1].WaitOffsetPercent, 3);
        Assert.Equal(50.0 / 3, layout.Bars[1].WaitWidthPercent, 3);
    }

    [Fact]
    public void WithoutADepthTheWaitRunsFromThePreviousStageThatHadFinished()
    {
        // No definition snapshot: the last stage listed before it that had ended by its start. The
        // one still running at that moment (Slow) is not what it waited for.
        var layout = Build(
            Stage("Fast", 0, 40),
            Stage("Slow", 0, 200),
            Stage("After", 60, 100));

        Assert.Equal(TimeSpan.FromSeconds(20), layout.Bars[2].Wait);
    }

    [Fact]
    public void TheFirstStageStartsTheAxisAndWaitsForNothing()
    {
        var layout = Build(Stage("Only", 0, 100, depth: 0));

        Assert.Equal(TimeSpan.Zero, layout.Bars[0].Wait);
        Assert.False(layout.Bars[0].HasWait);
    }

    [Fact]
    public void ForStagesRunOneAfterTheOtherExecutionPlusWaitCoversTheWholeAxis()
    {
        // The totals under the strip add up to the run's span when nothing overlapped.
        var layout = Build(
            Stage("Prepare", 0, 3),
            Stage("Verify", 10, 14, depth: 0),
            Stage("Deploy", 20, 25, depth: 1),
            Stage("Cleanup", 49, 49));

        var totals = RunGanttChart.Totals(layout);

        Assert.Equal(TimeSpan.FromSeconds(12), totals.Execution);
        Assert.Equal(TimeSpan.FromSeconds(37), totals.Wait);
        Assert.Equal(layout.Duration, totals.Execution + totals.Wait);
    }

    [Fact]
    public void OverlappingStagesCountTheirTimeOnceEach()
    {
        var layout = Build(Stage("A", 0, 100, depth: 0), Stage("B", 0, 100, depth: 0));

        var totals = RunGanttChart.Totals(layout);

        Assert.Equal(TimeSpan.FromSeconds(200), totals.Execution);
        Assert.Equal(TimeSpan.Zero, totals.Wait);
    }
}
