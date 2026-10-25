// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>A copied nightly or webhook pipeline is written with a manual trigger, so it never starts
/// on its own in the new project; the rest of the definition is kept so enabling it is one line.</summary>
public sealed class PipelineCopyTriggersTests
{
    [Theory]
    [InlineData("trigger: schedule", "schedule")]
    [InlineData("trigger: webhook", "webhook")]
    [InlineData("trigger: \"schedule\"", "schedule")]
    [InlineData("trigger: 'webhook'   # nightly", "webhook")]
    [InlineData("trigger: Schedule", "schedule")]
    public void Disable_SwitchesAnAutomaticTriggerToManual_AndReportsTheOriginal(string line, string expected)
    {
        var yaml = $"name: nightly\n{line}\nschedule: \"0 0 3 * * *\"\nstages: []\n";

        var (disabled, original) = PipelineCopyTriggers.Disable(yaml);

        Assert.Equal(expected, original);
        Assert.Contains("trigger: manual", disabled, StringComparison.Ordinal);
        Assert.Contains($"was '{expected}'", disabled, StringComparison.Ordinal);
        // Nothing else moves: the schedule stays, so setting trigger back is enough to enable it.
        Assert.Contains("schedule: \"0 0 3 * * *\"", disabled, StringComparison.Ordinal);
        Assert.False(PipelineCopyTriggers.IsActive(disabled));
        Assert.Equal("manual", YamlParsingHelper.ParseAndValidate(disabled)!.Trigger);
    }

    [Fact]
    public void Disable_KeepsCrLfLineEndings()
    {
        var (disabled, original) = PipelineCopyTriggers.Disable("name: x\r\ntrigger: webhook\r\nstages: []\r\n");

        Assert.Equal("webhook", original);
        Assert.Equal("name: x\r\ntrigger: manual # copy: was 'webhook', set it back to enable\r\nstages: []\r\n", disabled);
    }

    [Theory]
    [InlineData("name: x\ntrigger: manual\nstages: []\n")]
    [InlineData("name: x\nstages: []\n")]
    public void Disable_LeavesAManualPipelineAlone(string yaml)
    {
        var (disabled, original) = PipelineCopyTriggers.Disable(yaml);

        Assert.Null(original);
        Assert.Equal(yaml, disabled);
    }

    [Fact]
    public void Disable_OnlyReadsTheTopLevelKey()
    {
        const string yaml = "name: x\nstages:\n  - name: a\n    variables:\n      trigger: schedule\n";

        var (disabled, original) = PipelineCopyTriggers.Disable(yaml);

        Assert.Null(original);
        Assert.Equal(yaml, disabled);
    }

    [Fact]
    public void IsActive_FlagsATriggerTheRewriteDoesNotRecognise()
    {
        // Flow-style mapping: the line rewrite does not match it, and the definition still runs on its own.
        const string yaml = "{name: x, trigger: schedule, schedule: '0 0 3 * * *', stages: []}";

        var (disabled, original) = PipelineCopyTriggers.Disable(yaml);

        Assert.Null(original);
        Assert.True(PipelineCopyTriggers.IsActive(disabled));
    }
}
