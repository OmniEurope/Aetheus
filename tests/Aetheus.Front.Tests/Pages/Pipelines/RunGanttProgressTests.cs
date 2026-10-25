// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Covers aligned progression metrics and running-stage fill before and beyond usual
/// duration.</summary>
public sealed class RunGanttProgressTests : BunitContext
{
    private static readonly DateTime Origin = new(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);

    public RunGanttProgressTests() => BunitTestHelper.RegisterServices(this);

    [Theory]
    [InlineData(30, 100, 30, 0)]    // a third of the way: filled to 30 %, no overrun
    [InlineData(100, 100, 100, 0)]  // exactly the usual duration
    [InlineData(150, 100, 100, 50)] // half again as long: the second layer is half-way
    [InlineData(400, 100, 100, 100)] // far beyond: both layers full, the column says how much
    public void Progress_FillsToTheAverage_ThenRestartsFromTheLeft(int elapsed, double average, double fill, double overrun)
    {
        var progress = RunGanttProgress.Of(TimeSpan.FromSeconds(elapsed), average)!;

        Assert.Equal(fill, progress.FillPercent, precision: 6);
        Assert.Equal(overrun, progress.OverrunPercent, precision: 6);
        Assert.Equal(overrun > 0, progress.IsOverrunning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public void Progress_WithNoUsualDuration_IsNotDrawn(double? average) =>
        Assert.Null(RunGanttProgress.Of(TimeSpan.FromSeconds(10), average));

    private IRenderedComponent<RunGanttStrip> RenderRun(PipelineRunDto run, RunStageBaselinesDto? baselines, int nowSeconds = 1000) =>
        Render<RunGanttStrip>(parameters => parameters
            .Add(p => p.Run, run)
            .Add(p => p.Baselines, baselines)
            .Add(p => p.Clock, new FakeTimeProvider(Origin.AddSeconds(nowSeconds))));

    private static PipelineStepRunDto Step(int id, string stage, int start, int? end, TaskExecutionStatus status = TaskExecutionStatus.Success) => new()
    {
        Id = id,
        StageName = stage,
        StepName = $"step {id}",
        Status = status,
        StartedAt = Origin.AddSeconds(start),
        CompletedAt = end is { } e ? Origin.AddSeconds(e) : null
    };

    private static List<string> Headers(IRenderedComponent<RunGanttStrip> cut) =>
        [.. cut.FindAll(".run-gantt-head .run-gantt-cols > span").Select(span => span.GetAttribute("data-column")!)];

    [Fact]
    public void WithoutHistoryOrMetrics_OnlyTheDurationColumnIsDrawn()
    {
        var cut = RenderRun(new PipelineRunDto { Id = 1, Steps = [Step(1, "Build", 0, 100)] }, baselines: null);

        Assert.Equal(["Duration"], Headers(cut));
    }

    [Fact]
    public void WithHistoryAndMetrics_EveryColumnIsDrawn_AndAStageSumsItsStepsCpu()
    {
        var run = new PipelineRunDto
        {
            Id = 1,
            Steps = [Step(1, "Build", 0, 100), Step(2, "Build", 0, 80)],
            Metrics =
            [
                new RunMetricDto { StageName = "Build", StepName = "step 1", Key = "step.cpu", Value = 30 },
                new RunMetricDto { StageName = "Build", StepName = "step 2", Key = "step.cpu", Value = 30 },
                new RunMetricDto { StageName = "Build", StepName = "step 1", Key = "step.disk.read", Value = 2048 }
            ]
        };
        var baselines = new RunStageBaselinesDto
        {
            SampleRuns = 10,
            Stages = [new StageBaselineDto { StageName = "build", AverageSeconds = 125, LastSeconds = 90, Samples = 10 }]
        };

        var cut = RenderRun(run, baselines);

        Assert.Equal(["Duration", "Average", "Last", "Cpu", "Io"], Headers(cut));
        var cells = cut.FindAll(".run-gantt-row:not(.run-gantt-head) .run-gantt-col").Select(cell => cell.TextContent).ToList();
        Assert.Equal("1min40s", cells[0]); // the stage's own duration reads in the same precision (R-23)
        Assert.Equal("2min05s", cells[1]); // the measurement columns keep their seconds
        Assert.Equal("1min30s", cells[2]);
        Assert.Equal("1min00s", cells[3]); // 30 s + 30 s of CPU across the stage's two steps
    }

    [Fact]
    public void ARunningStagePastItsAverage_FillsThenOverruns_AndAFinishedOneIsNotFilled()
    {
        var run = new PipelineRunDto
        {
            Id = 1,
            Steps =
            [
                Step(1, "Build", 0, 100),
                Step(2, "Test", 100, null, TaskExecutionStatus.Running)
            ]
        };
        var baselines = new RunStageBaselinesDto
        {
            SampleRuns = 5,
            Stages =
            [
                new StageBaselineDto { StageName = "Build", AverageSeconds = 100, LastSeconds = 100, Samples = 5 },
                new StageBaselineDto { StageName = "Test", AverageSeconds = 200, LastSeconds = 200, Samples = 5 }
            ]
        };

        // Test has run 300 s against a usual 200 s: full fill, overrun at 50 %.
        var cut = RenderRun(run, baselines, nowSeconds: 400);

        var fill = Assert.Single(cut.FindAll(".run-gantt-progress"));
        var runningBar = Assert.Single(cut.FindAll(".run-gantt-running"));
        Assert.Equal(runningBar.GetAttribute("width"), fill.GetAttribute("width"));
        var overrun = Assert.Single(cut.FindAll(".run-gantt-overrun"));
        Assert.Equal(
            double.Parse(fill.GetAttribute("width")!, System.Globalization.CultureInfo.InvariantCulture) / 2,
            double.Parse(overrun.GetAttribute("width")!, System.Globalization.CultureInfo.InvariantCulture),
            precision: 6);
        Assert.Contains("GanttOverAverage", cut.FindAll(".run-gantt-track")[1].GetAttribute("title"), StringComparison.Ordinal);
    }
}
