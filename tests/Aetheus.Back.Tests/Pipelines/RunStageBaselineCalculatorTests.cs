// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Duration-baseline arithmetic.</summary>
public sealed class RunStageBaselineCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private static StepTimingRow Row(int run, int runHour, string stage, string step, int startSec, int endSec, string? leg = null) =>
        new(run, T0.AddHours(runHour), stage, step, leg, false, T0.AddHours(runHour).AddSeconds(startSec), T0.AddHours(runHour).AddSeconds(endSec));

    [Fact]
    public void ARetriedStep_CountsOnce_WithItsLatestAttempt()
    {
        var baselines = RunStageBaselineCalculator.Compute(
        [
            Row(1, 0, "Test", "Unit", 0, 500),   // first attempt, failed and retried
            Row(1, 0, "Test", "Unit", 600, 700), // the attempt that succeeded: 100 s
            Row(2, 1, "Test", "Unit", 0, 300)
        ]);

        var step = Assert.Single(baselines.Steps);
        Assert.Equal(2, step.Samples);
        Assert.Equal(200, step.AverageSeconds, precision: 3);
        Assert.Equal(300, step.LastSeconds, precision: 3); // run 2 is the most recent
    }

    [Fact]
    public void AStage_LastsFromItsFirstStartToItsLastEnd_NotTheSumOfParallelSteps()
    {
        var baselines = RunStageBaselineCalculator.Compute(
        [
            Row(1, 0, "Checks", "Lint", 0, 60),
            Row(1, 0, "Checks", "Sast", 0, 90)
        ]);

        Assert.Equal(90, Assert.Single(baselines.Stages).AverageSeconds, precision: 3);
        Assert.Equal(2, baselines.Steps.Count);
    }

    [Fact]
    public void MatrixLegs_AreSeparateSteps_AndCaseDoesNotSplitAName()
    {
        var baselines = RunStageBaselineCalculator.Compute(
        [
            Row(1, 0, "Build", "Compile", 0, 10, "linux"),
            Row(1, 0, "Build", "Compile", 0, 30, "windows"),
            Row(2, 1, "build", "compile", 0, 20, "LINUX ")
        ]);

        Assert.Equal(2, baselines.Steps.Count);
        Assert.Equal(15, baselines.Steps.Single(step => step.MatrixLeg!.Trim().Equals("linux", StringComparison.OrdinalIgnoreCase)).AverageSeconds, precision: 3);
    }

    [Fact]
    public void NoSampledRun_GivesNoFigure()
    {
        var baselines = RunStageBaselineCalculator.Compute([]);

        Assert.Equal(0, baselines.SampleRuns);
        Assert.Empty(baselines.Steps);
        Assert.Empty(baselines.Stages);
    }
}
