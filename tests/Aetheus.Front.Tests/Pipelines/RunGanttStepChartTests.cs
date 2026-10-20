// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary> A stage unfolded into its steps, on the same axis as the stage bars. The cases that
/// matter are the ones where a step bar could lie: a step that never started, one still running,
/// and the legs of a matrix step that would otherwise read as one step drawn twice. </summary>
public sealed class RunGanttStepChartTests
{
    private static readonly DateTime Origin = new(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);

    private static PipelineStepRunDto Step(
        string name, int? startSeconds, int? endSeconds,
        TaskExecutionStatus status = TaskExecutionStatus.Success, string? leg = null) => new()
        {
            StepName = name,
            StageName = "Build",
            Status = status,
            MatrixLeg = leg,
            StartedAt = startSeconds is { } s ? Origin.AddSeconds(s) : null,
            CompletedAt = endSeconds is { } e ? Origin.AddSeconds(e) : null
        };

    private static StageViewModel Stage(string name, int? start, int? end, params PipelineStepRunDto[] steps) => new()
    {
        Name = name,
        DisplayName = name,
        Status = end is null ? TaskExecutionStatus.Running : TaskExecutionStatus.Success,
        StartedAt = start is { } s ? Origin.AddSeconds(s) : null,
        CompletedAt = end is { } e ? Origin.AddSeconds(e) : null,
        Steps = [.. steps]
    };

    [Fact]
    public void AStepInsideItsStageIsPlacedOnTheRunsAxisNotOnTheStages()
    {
        // Axis 0..200 s; the Build stage runs 100..200 and its second step 150..200. On the shared
        // axis that step starts at 75 % and is 25 % wide, inside its stage's bar (50 %, 50 %).
        var build = Stage("Build", 100, 200, Step("restore", 100, 150), Step("compile", 150, 200));
        var layout = RunGanttChart.Build([Stage("Prepare", 0, 100), build], Origin.AddSeconds(1000))!;

        var steps = RunGanttStepChart.Build(build, layout, Origin.AddSeconds(1000));

        Assert.Equal(2, steps.Count);
        Assert.Equal(50, steps[0].OffsetPercent, 3);
        Assert.Equal(25, steps[0].WidthPercent, 3);
        Assert.Equal(75, steps[1].OffsetPercent, 3);
        Assert.Equal(25, steps[1].WidthPercent, 3);
        Assert.Equal(TimeSpan.FromSeconds(50), steps[1].Duration);
        var stageBar = layout.Bars[1];
        Assert.All(steps, step =>
        {
            Assert.True(step.OffsetPercent >= stageBar.OffsetPercent - 0.0001);
            Assert.True(step.OffsetPercent + step.WidthPercent <= stageBar.OffsetPercent + stageBar.WidthPercent + 0.0001);
        });
    }

    [Fact]
    public void AStepWithoutAStartIsListedWithItsStatusAndNoBar()
    {
        // Nothing simulated: a step that never started occupied no time on the axis.
        var build = Stage("Build", 0, 100,
            Step("compile", 0, 100),
            Step("publish", null, null, TaskExecutionStatus.Cancelled));
        var layout = RunGanttChart.Build([build], Origin.AddSeconds(1000))!;

        var steps = RunGanttStepChart.Build(build, layout, Origin.AddSeconds(1000));

        var publish = steps[1];
        Assert.False(publish.HasBar);
        Assert.Equal(0, publish.WidthPercent);
        Assert.Equal(TimeSpan.Zero, publish.Duration);
        Assert.Equal(TaskExecutionStatus.Cancelled, publish.Step.Status);
        Assert.True(steps[0].HasBar);
    }

    [Fact]
    public void ARunningStepIsDrawnUpToNow()
    {
        // Now is 300 s; the running stage stretches the axis to it, and its running step reaches the
        // same right edge: 100..300 on a 0..300 axis.
        var now = Origin.AddSeconds(300);
        var test = Stage("Test", 100, null, Step("unit", 100, null, TaskExecutionStatus.Running));
        var layout = RunGanttChart.Build([Stage("Build", 0, 100), test], now)!;

        var step = Assert.Single(RunGanttStepChart.Build(test, layout, now));

        Assert.True(step.IsRunning);
        Assert.Equal(100.0 / 3, step.OffsetPercent, 3);
        Assert.Equal(200.0 / 3, step.WidthPercent, 3);
        Assert.Equal(TimeSpan.FromSeconds(200), step.Duration);
        Assert.Equal(100, step.OffsetPercent + step.WidthPercent, 3);
    }

    [Fact]
    public void ARunningStepNeverRunsPastTheAxis()
    {
        // The stage claims to be finished at 100 s while one of its steps has no end: the step stops
        // at the axis end instead of overflowing the chart.
        var build = Stage("Build", 0, 100, Step("hung", 50, null, TaskExecutionStatus.Running));
        var layout = RunGanttChart.Build([build], Origin.AddSeconds(1000))!;

        var step = Assert.Single(RunGanttStepChart.Build(build, layout, Origin.AddSeconds(1000)));

        Assert.True(step.OffsetPercent + step.WidthPercent <= 100.0001);
        Assert.Equal(50, step.OffsetPercent, 3);
    }

    [Fact]
    public void MatrixLegsAreLabelledApartAndEachGetsItsOwnBar()
    {
        var build = Stage("Build", 0, 100,
            Step("test", 0, 60, leg: "linux-x64"),
            Step("test", 0, 100, TaskExecutionStatus.Failed, leg: "win-x64"),
            Step("pack", 60, 80));
        var layout = RunGanttChart.Build([build], Origin.AddSeconds(1000))!;

        var steps = RunGanttStepChart.Build(build, layout, Origin.AddSeconds(1000));

        Assert.Equal(["test (linux-x64)", "test (win-x64)", "pack"], steps.Select(step => step.Label));
        Assert.Equal(60, steps[0].WidthPercent, 3);
        Assert.Equal(100, steps[1].WidthPercent, 3);
        Assert.Equal(0, steps[0].OffsetPercent, 3);
        Assert.Equal(0, steps[1].OffsetPercent, 3);
    }

    [Fact]
    public void ABlankMatrixLegAddsNothingToTheLabel()
    {
        Assert.Equal("compile", RunGanttStepChart.LabelOf(Step("compile", 0, 1, leg: "  ")));
    }

    [Fact]
    public void AVeryShortStepStaysVisible()
    {
        var build = Stage("Build", 0, 1000, Step("blink", 0, 1), Step("long", 1, 1000));
        var layout = RunGanttChart.Build([build], Origin.AddSeconds(1000))!;

        var steps = RunGanttStepChart.Build(build, layout, Origin.AddSeconds(1000));

        Assert.Equal(RunGanttChart.MinimumBarWidthPercent, steps[0].WidthPercent, 3);
    }
}
