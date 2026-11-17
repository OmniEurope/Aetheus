// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

public sealed class PipelineScheduleReaderTests
{
    [Fact]
    public void Read_ScheduledPipeline_ReturnsTriggerAndCron()
    {
        var yaml = """
            name: nightly
            trigger: schedule
            # 00:00 UTC
            schedule: "0 0 0 * * *"
            stages: []
            """;

        var info = PipelineScheduleReader.Read(yaml);

        Assert.NotNull(info);
        Assert.Equal("schedule", info.Trigger);
        Assert.Equal("0 0 0 * * *", info.Schedule);
        Assert.True(info.IsScheduled);
    }

    // Disabling keeps the value: the planner can still show and re-enable it.
    [Fact]
    public void Read_ManualPipelineKeepingItsSchedule_IsNotScheduledButKeepsTheValue()
    {
        var info = PipelineScheduleReader.Read("name: demo\ntrigger: manual\nschedule: '0 0 2 * * *'\n");

        Assert.NotNull(info);
        Assert.Equal("manual", info.Trigger);
        Assert.Equal("0 0 2 * * *", info.Schedule);
        Assert.False(info.IsScheduled);
    }

    [Fact]
    public void Read_NoTriggerNoSchedule_DefaultsToManualWithoutSchedule()
    {
        var info = PipelineScheduleReader.Read("name: plain\n");

        Assert.NotNull(info);
        Assert.Equal("manual", info.Trigger);
        Assert.Null(info.Schedule);
        Assert.False(info.IsScheduled);
    }

    [Fact]
    public void Read_BlankSchedule_IsNull()
    {
        var info = PipelineScheduleReader.Read("name: x\ntrigger: schedule\nschedule: \"   \"\n");

        Assert.NotNull(info);
        Assert.Null(info.Schedule);
    }

    [Fact]
    public void Read_UnknownKeysAreIgnored()
    {
        var info = PipelineScheduleReader.Read("name: x\nsomething_new: 1\ntrigger: Schedule\nschedule: \"0 15 3 * * 0\"\n");

        Assert.NotNull(info);
        Assert.Equal("0 15 3 * * 0", info.Schedule);
        Assert.True(info.IsScheduled);
    }

    [Theory]
    [InlineData("name: [unclosed")]
    [InlineData("name: x\n  trigger: schedule\n schedule: \"0 0 0 * * *\"")]
    [InlineData("trigger: [a, b]\nschedule: { not: scalar }")]
    [InlineData("{{{{")]
    [InlineData("- just\n- a list\n")]
    [InlineData("name: a: b")]
    public void Read_InvalidYaml_ReturnsNullAndNeverThrows(string yaml)
    {
        Assert.Null(PipelineScheduleReader.Read(yaml));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Read_EmptyInput_ReturnsNull(string? yaml)
    {
        Assert.Null(PipelineScheduleReader.Read(yaml));
    }

    // The reader sees only the pipeline's own document, as the scheduler engine does.
    [Fact]
    public void Read_ScheduleOnlyInAnExtendedParent_IsNotSeen()
    {
        var info = PipelineScheduleReader.Read("name: child\nextends: nightly-template\ntrigger: schedule\n");

        Assert.NotNull(info);
        Assert.True(info.IsScheduled);
        Assert.Null(info.Schedule);
    }
}
