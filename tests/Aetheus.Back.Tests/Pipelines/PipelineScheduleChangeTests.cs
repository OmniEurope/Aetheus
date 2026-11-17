// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The checks made before any write: the expression the engine will fire, and the YAML the
/// engine will read back. Nothing here writes anything: the change is only prepared.</summary>
public sealed class PipelineScheduleChangeTests
{
    private const string Yaml = "name: n\ntrigger: manual\nstages: []\n";

    private static ScheduleFrequencySpec Nightly(int hour = 1, int minute = 30) =>
        new(ScheduleFrequency.EveryEvening, null, hour, minute);

    [Fact]
    public void ValidProposal_GivesTheExpression_AndAYamlTheEngineReadsBack()
    {
        var change = PipelineScheduleChange.Prepare(Yaml, Nightly(), enabled: true);

        Assert.True(change.Succeeded, change.Error);
        Assert.Equal("0 30 1 * * *", change.Cron);
        var read = YamlParsingHelper.ParseAndValidate(change.Yaml!)!;
        Assert.Equal(("schedule", "0 30 1 * * *"), (read.Trigger, read.Schedule));
    }

    [Fact]
    public void Disabled_KeepsTheValue_AndLeavesTheTriggerManual()
    {
        var change = PipelineScheduleChange.Prepare(
            "name: n\ntrigger: schedule\nschedule: \"0 0 0 * * *\"\n", Nightly(), enabled: false);

        Assert.True(change.Succeeded, change.Error);
        var read = YamlParsingHelper.ParseAndValidate(change.Yaml!)!;
        Assert.Equal(("manual", "0 30 1 * * *"), (read.Trigger, read.Schedule));
    }

    [Theory]
    [InlineData(ScheduleFrequency.Custom, null, 1, 30)]
    [InlineData(ScheduleFrequency.EveryEvening, null, 24, 0)]
    [InlineData(ScheduleFrequency.EveryEvening, null, 1, 60)]
    [InlineData(ScheduleFrequency.Weekly, null, 1, 0)]
    [InlineData(ScheduleFrequency.Weekly, 7, 1, 0)]
    [InlineData(ScheduleFrequency.Monthly, 29, 1, 0)]
    public void ProposalTheCodecCannotWrite_IsRefused_AndNoYamlIsGiven(ScheduleFrequency frequency, int? day, int hour, int minute)
    {
        var change = PipelineScheduleChange.Prepare(Yaml, new ScheduleFrequencySpec(frequency, day, hour, minute), enabled: true);

        Assert.False(change.Succeeded);
        Assert.Null(change.Yaml);
        Assert.Null(change.Cron);
        Assert.Contains("cannot be written", change.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0 3 * * *")]
    [InlineData("99 99 99 * * *")]
    [InlineData("0 0 25 * * *")]
    public void ExpressionCronosRefuses_IsRefusedBeforeTheYamlIsTouched(string cron)
    {
        var change = PipelineScheduleChange.PrepareExpression(Yaml, cron, enabled: true);

        Assert.False(change.Succeeded);
        Assert.Null(change.Yaml);
        Assert.Contains("not a cron expression the engine can fire", change.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionCronosAccepts_PassesThroughTheSameChecks()
    {
        var change = PipelineScheduleChange.PrepareExpression(Yaml, "0 0 2 * * 1-5", enabled: true);

        Assert.True(change.Succeeded, change.Error);
        Assert.Equal("0 0 2 * * 1-5", YamlParsingHelper.ParseAndValidate(change.Yaml!)!.Schedule);
    }

    // The text edit alone is happy with this document; the semantic check of the pipeline parser is not.
    [Fact]
    public void YamlThePipelineParserRefuses_IsRefused_WithItsReason()
    {
        var change = PipelineScheduleChange.Prepare(
            "name: n\ntrigger: manual\nvariable_libraries:\n  - \"\"\nstages: []\n", Nightly(), enabled: true);

        Assert.False(change.Succeeded);
        Assert.Null(change.Yaml);
        Assert.Contains("variable_libraries", change.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorRefusal_IsReported_AsTheWholeChangeRefused()
    {
        var change = PipelineScheduleChange.Prepare(
            "name: n\nschedule: \"0 0 1 * * *\"\nschedule: \"0 0 2 * * *\"\n", Nightly(), enabled: true);

        Assert.False(change.Succeeded);
        Assert.Null(change.Yaml);
        Assert.Contains("more than once", change.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabling_KeepsTheValueAndAllTheRestOfTheText_AndEnablingPutsItBack()
    {
        const string scheduled = "# nightly\nname: n\ntrigger: schedule\nschedule: '0 0 0 * * *' # 00:00 UTC\nstages: []\n";

        var disabled = PipelineScheduleChange.PrepareEnabled(scheduled, enabled: false);
        var enabled = PipelineScheduleChange.PrepareEnabled(disabled.Yaml!, enabled: true);

        Assert.Equal(scheduled.Replace("trigger: schedule", "trigger: manual", StringComparison.Ordinal), disabled.Yaml);
        Assert.Null(disabled.Cron);
        Assert.Equal(scheduled, enabled.Yaml);
        Assert.Equal("0 0 0 * * *", enabled.Cron);
    }

    [Fact]
    public void Disabling_AValueTheEngineCannotRead_IsAllowed_EnablingItIsNot()
    {
        const string broken = "name: n\ntrigger: schedule\nschedule: \"abc\"\nstages: []\n";

        var disabled = PipelineScheduleChange.PrepareEnabled(broken, enabled: false);
        var enabled = PipelineScheduleChange.PrepareEnabled(disabled.Yaml!, enabled: true);

        Assert.True(disabled.Succeeded, disabled.Error);
        Assert.Contains("not a cron expression the engine can fire", enabled.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabling_WithoutAValue_IsRefused()
    {
        var change = PipelineScheduleChange.PrepareEnabled(Yaml, enabled: true);

        Assert.Null(change.Yaml);
        Assert.Contains("no schedule", change.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabling_APipelineWithoutTrigger_WritesTheSameText()
    {
        const string noTrigger = "name: n\nstages: []\n";

        Assert.Equal(noTrigger, PipelineScheduleChange.PrepareEnabled(noTrigger, enabled: false).Yaml);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WebhookPipeline_IsNotTakenOverByTheSchedule(bool enabled)
    {
        const string webhook = "name: n\ntrigger: webhook\nschedule: \"0 0 1 * * *\"\nstages: []\n";

        Assert.Contains("webhook", PipelineScheduleChange.Prepare(webhook, Nightly(), enabled).Error, StringComparison.Ordinal);
        Assert.Contains("webhook", PipelineScheduleChange.PrepareEnabled(webhook, enabled).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void YamlThatDoesNotParse_IsRefused()
    {
        var change = PipelineScheduleChange.Prepare("name: [unclosed\n", Nightly(), enabled: true);

        Assert.False(change.Succeeded);
        Assert.Contains("does not parse", change.Error, StringComparison.Ordinal);
    }
}
