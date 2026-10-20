// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>What a step consumed, from its <c>step.*</c> metrics: CPU and disk add up over a stage, peak
/// memory does not, and only the measurements the agent published are shown.</summary>
public sealed class PipelineRunStepResourcesTests
{
    private static RunMetricDto Metric(string key, double value, string stage = "build", string step = "compile") =>
        new() { Key = key, Value = value, StageName = stage, StepName = step };

    private static PipelineRunDto Run(params RunMetricDto[] metrics) => new() { Metrics = [.. metrics] };

    [Fact]
    public void AStep_GetsOnlyItsOwnMetrics_MatchedWithoutCase()
    {
        var run = Run(
            Metric("step.cpu", 12, "BUILD", "Compile"),
            Metric("step.memory.peak", 2048),
            Metric("step.cpu", 99, "build", "test"));

        var usage = PipelineRunStepResources.ForStep(run, new PipelineStepRunDto { StageName = "build", StepName = "compile" });

        Assert.Equal(new PipelineRunStepResources.Usage(12, 2048, null), usage);
    }

    [Fact]
    public void AStage_SumsCpuAndDisk_ButKeepsThePeakMemory()
    {
        var run = Run(
            Metric("step.cpu", 10, step: "a"), Metric("step.cpu", 5, step: "b"),
            Metric("step.memory.peak", 2_000, step: "a"), Metric("step.memory.peak", 3_000, step: "b"),
            Metric("step.disk.read", 100, step: "a"), Metric("step.disk.written", 50, step: "b"),
            Metric("step.cpu", 1_000, stage: "deploy"));

        Assert.Equal(new PipelineRunStepResources.Usage(15, 3_000, 150), PipelineRunStepResources.ForStage(run, "Build"));
    }

    [Fact]
    public void NoRunNoMetricOrOnlyForeignKeys_IsNoUsage()
    {
        var step = new PipelineStepRunDto { StageName = "build", StepName = "compile" };

        Assert.Null(PipelineRunStepResources.ForStep(null, step));
        Assert.Null(PipelineRunStepResources.ForStage(null, "build"));
        Assert.Null(PipelineRunStepResources.ForStep(Run(), step));
        Assert.Null(PipelineRunStepResources.ForStep(Run(Metric("step.exit", 1)), step));
    }

    [Fact]
    public void Describe_ShowsEveryPublishedPartInOrder()
    {
        var line = PipelineRunStepResources.Describe(new PipelineRunStepResources.Usage(75, 480 * 1_048_576, 2_048));

        Assert.Equal($"cpu 1m 15s · {PipelineRunFormatting.FormatSize(480 * 1_048_576)} · io 2 KB", line);
    }

    [Fact]
    public void Describe_LeavesOutWhatWasNotMeasured_AndAnIdleDisk()
    {
        Assert.Equal("cpu 9s", PipelineRunStepResources.Describe(new PipelineRunStepResources.Usage(9, null, 0)));
        Assert.Equal("512 B", PipelineRunStepResources.Describe(new PipelineRunStepResources.Usage(null, 512, null)));
        Assert.Equal("io 3 KB", PipelineRunStepResources.Describe(new PipelineRunStepResources.Usage(null, null, 3_072)));
    }
}
