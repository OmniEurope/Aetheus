// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The text editor of <c>schedule:</c> and <c>trigger:</c>. YAML here is always a string
/// (no test reads a file of <c>.pipeline/</c>).</summary>
public sealed class PipelineScheduleYamlEditorTests
{
    private const string Cron = "0 30 1 * * *";

    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    private static string Edited(string yaml, string? cron, string? trigger)
    {
        var result = PipelineScheduleYamlEditor.Apply(yaml, cron, trigger);
        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Yaml);
        return result.Yaml;
    }

    private static string Refused(string yaml, string? cron, string? trigger)
    {
        var result = PipelineScheduleYamlEditor.Apply(yaml, cron, trigger);
        Assert.False(result.Succeeded);
        Assert.Null(result.Yaml);
        return result.Error!;
    }

    [Theory]
    [InlineData("\"0 0 0 * * *\"", "\"0 30 1 * * *\"")]
    [InlineData("'0 0 0 * * *'", "'0 30 1 * * *'")]
    [InlineData("0 0 0 * * *", "0 30 1 * * *")]
    public void ReplacesTheValue_KeepingItsQuoteStyle(string written, string expected)
    {
        var yaml = Lines("name: nightly", "trigger: schedule", $"schedule: {written}", "stages: []");

        var result = Edited(yaml, Cron, null);

        Assert.Equal(Lines("name: nightly", "trigger: schedule", $"schedule: {expected}", "stages: []"), result);
    }

    [Fact]
    public void EndOfLineComment_KeepsItsPlaceAndSpacing()
    {
        var yaml = Lines("name: n", "schedule: \"0 0 0 * * *\"   # 00:00 UTC", "stages: []");

        Assert.Equal(
            Lines("name: n", "schedule: \"0 30 1 * * *\"   # 00:00 UTC", "stages: []"),
            Edited(yaml, Cron, null));
    }

    [Fact]
    public void PlainValueWithComment_StopsAtTheComment()
    {
        var yaml = Lines("name: n", "schedule: 0 0 0 * * * # nightly", "stages: []");

        Assert.Equal(Lines("name: n", "schedule: 0 30 1 * * * # nightly", "stages: []"), Edited(yaml, Cron, null));
    }

    [Fact]
    public void NeighboringComments_AreUntouched()
    {
        var yaml = Lines(
            "# header", "name: n", "# the engine reads this in UTC", "schedule: \"0 0 0 * * *\"", "# after", "trigger: schedule", "stages: []");

        var result = Edited(yaml, Cron, null);

        Assert.Equal(
            Lines("# header", "name: n", "# the engine reads this in UTC", "schedule: \"0 30 1 * * *\"", "# after", "trigger: schedule", "stages: []"),
            result);
    }

    [Fact]
    public void AbsentKey_IsAddedAfterTrigger_WithDoubleQuotes()
    {
        var yaml = Lines("name: n", "trigger: schedule", "# stages follow", "stages: []");

        var result = Edited(yaml, Cron, null);

        Assert.Equal(Lines("name: n", "trigger: schedule", "schedule: \"0 30 1 * * *\"", "# stages follow", "stages: []"), result);
        Assert.Equal(Cron, YamlParsingHelper.ParseAndValidate(result)!.Schedule);
    }

    [Fact]
    public void AbsentKeys_PlanAManualPipeline_AddsTriggerThenSchedule()
    {
        var yaml = Lines("name: deploy", "trigger: manual", "stages: []");

        var result = Edited(yaml, Cron, "schedule");

        Assert.Equal(Lines("name: deploy", "trigger: schedule", "schedule: \"0 30 1 * * *\"", "stages: []"), result);
    }

    [Fact]
    public void AbsentTriggerAndSchedule_AreAddedAfterTheName()
    {
        var yaml = Lines("name: bare", "stages: []");

        var result = Edited(yaml, Cron, "schedule");

        Assert.Equal(Lines("name: bare", "trigger: schedule", "schedule: \"0 30 1 * * *\"", "stages: []"), result);
        var parsed = YamlParsingHelper.ParseAndValidate(result)!;
        Assert.Equal(("schedule", Cron), (parsed.Trigger, parsed.Schedule));
    }

    [Fact]
    public void NoNameNoTrigger_AppendsAfterTheLastEntry_WithoutCuttingABlockScalar()
    {
        var yaml = Lines("stages:", "  - name: build", "    steps:", "      - shell: |", "          echo a", "          # a trailing comment of the script");

        var result = Edited(yaml, Cron, null);

        Assert.StartsWith(yaml, result, StringComparison.Ordinal);
        Assert.EndsWith("schedule: \"0 30 1 * * *\"\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoScheduleKeys_AreRefused_AndNothingIsReturned()
    {
        var yaml = Lines("name: n", "schedule: \"0 0 0 * * *\"", "schedule: \"0 0 2 * * *\"", "stages: []");

        Assert.Contains("more than once", Refused(yaml, Cron, null), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoTriggerKeys_AreRefused()
    {
        var yaml = Lines("name: n", "trigger: manual", "trigger: schedule", "stages: []");

        Assert.Contains("more than once", Refused(yaml, null, "manual"), StringComparison.Ordinal);
    }

    // The text scan does not recognize a quoted key: the comparison of the parsed documents is what stops it.
    [Theory]
    [InlineData("\"schedule\": \"0 0 0 * * *\"")]
    [InlineData("'trigger': manual")]
    public void QuotedKeyTheScanDoesNotSee_IsNeverDuplicated(string quotedKeyLine)
    {
        var yaml = Lines("name: n", quotedKeyLine, "stages: []");

        var result = PipelineScheduleYamlEditor.Apply(yaml, Cron, "schedule");

        Assert.False(result.Succeeded);
        Assert.Null(result.Yaml);
    }

    [Fact]
    public void InvalidYaml_IsRefused_WithTheParserMessage()
    {
        var error = Refused("name: [unclosed\nschedule: \"0 0 0 * * *\"\n", Cron, null);

        Assert.Contains("does not parse", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CrLf_IsKeptOnEveryLine_AndOnTheInsertedOnes()
    {
        var yaml = "name: n\r\ntrigger: manual\r\n# c\r\nschedule: \"0 0 0 * * *\" # x\r\nstages: []\r\n";

        var replaced = Edited(yaml, Cron, "schedule");
        var added = Edited("name: n\r\ntrigger: schedule\r\nstages: []\r\n", Cron, null);

        Assert.Equal("name: n\r\ntrigger: schedule\r\n# c\r\nschedule: \"0 30 1 * * *\" # x\r\nstages: []\r\n", replaced);
        Assert.Equal("name: n\r\ntrigger: schedule\r\nschedule: \"0 30 1 * * *\"\r\nstages: []\r\n", added);
        Assert.DoesNotContain("\n", added.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduleInsideAScriptBlock_IsNotTheKey()
    {
        var yaml = Lines(
            "name: n", "trigger: schedule", "schedule: \"0 0 0 * * *\"", "stages:", "  - name: build", "    steps:",
            "      - shell: |", "          echo start", "          schedule: \"not a cron\"", "          trigger: manual");

        var result = Edited(yaml, Cron, "manual");

        Assert.Equal(yaml.Replace("\"0 0 0 * * *\"", "\"0 30 1 * * *\"", StringComparison.Ordinal)
            .Replace("trigger: schedule", "trigger: manual", StringComparison.Ordinal), result);
    }

    [Fact]
    public void ScheduleOnlyInAScriptBlockAndAStep_IsAbsent_SoTheTopLevelKeyIsAdded()
    {
        var yaml = Lines(
            "name: n", "trigger: schedule", "stages:", "  - name: build", "    schedule: nested", "    steps:",
            "      - shell: |", "          schedule: \"inside a script\"");

        var result = Edited(yaml, Cron, null);

        Assert.Contains("    schedule: nested\n", result, StringComparison.Ordinal);
        Assert.Contains("          schedule: \"inside a script\"\n", result, StringComparison.Ordinal);
        Assert.Equal(Cron, YamlParsingHelper.ParseAndValidate(result)!.Schedule);
    }

    [Fact]
    public void ScheduleInATemplateBlock_IsNotTheTopLevelKey()
    {
        var yaml = Lines("name: child", "extends: parent", "parameters:", "  schedule: \"0 0 5 * * *\"", "trigger: manual");

        var result = Edited(yaml, Cron, null);

        Assert.Equal(
            Lines("name: child", "extends: parent", "parameters:", "  schedule: \"0 0 5 * * *\"", "trigger: manual", "schedule: \"0 30 1 * * *\""),
            result);
    }

    [Fact]
    public void Trigger_ScheduleToManual_KeepsTheScheduleValue()
    {
        var yaml = Lines("name: n", "trigger: schedule # live", "schedule: \"0 0 0 * * *\"", "stages: []");

        var result = Edited(yaml, null, "manual");

        Assert.Equal(Lines("name: n", "trigger: manual # live", "schedule: \"0 0 0 * * *\"", "stages: []"), result);
        var parsed = YamlParsingHelper.ParseAndValidate(result)!;
        Assert.Equal(("manual", "0 0 0 * * *"), (parsed.Trigger, parsed.Schedule));
    }

    [Fact]
    public void Trigger_QuotedAndOtherKindOfTrigger_SwitchToSchedule()
    {
        Assert.Equal(
            Lines("name: n", "trigger: \"schedule\""),
            Edited(Lines("name: n", "trigger: \"webhook\""), null, "schedule"));
    }

    [Fact]
    public void Trigger_ManualWhenTheKeyIsAbsent_ChangesNothing()
    {
        var yaml = Lines("name: n", "stages: []");

        Assert.Equal(yaml, Edited(yaml, null, "manual"));
    }

    [Fact]
    public void IndentedRoot_KeepsItsIndentation_WhenReplacingAndAdding()
    {
        var replaced = Edited(Lines("  name: n", "  schedule: \"0 0 0 * * *\"", "  stages:", "    - name: a"), Cron, null);
        var added = Edited(Lines("  name: n", "  trigger: schedule", "  stages:", "    - name: a"), Cron, null);

        Assert.Equal(Lines("  name: n", "  schedule: \"0 30 1 * * *\"", "  stages:", "    - name: a"), replaced);
        Assert.Equal(Lines("  name: n", "  trigger: schedule", "  schedule: \"0 30 1 * * *\"", "  stages:", "    - name: a"), added);
    }

    [Fact]
    public void SequenceAtTheRootIndent_BelongsToItsKey_AndIsNeverCut()
    {
        var yaml = Lines("name: n", "stages:", "- name: a", "  steps: []", "trigger: schedule", "variable_libraries:", "- x");

        var result = Edited(yaml, Cron, null);

        Assert.Equal(
            Lines("name: n", "stages:", "- name: a", "  steps: []", "trigger: schedule", "schedule: \"0 30 1 * * *\"", "variable_libraries:", "- x"),
            result);
    }

    [Fact]
    public void NullValue_IsFilled_AndItsCommentKept()
    {
        var empty = Edited(Lines("name: n", "schedule:", "stages: []"), Cron, null);
        var commented = Edited(Lines("name: n", "schedule: # set me", "stages: []"), Cron, null);

        Assert.Equal(Lines("name: n", "schedule: \"0 30 1 * * *\"", "stages: []"), empty);
        Assert.Equal(Lines("name: n", "schedule: \"0 30 1 * * *\" # set me", "stages: []"), commented);
    }

    [Fact]
    public void LeadingDocumentMarker_IsKept_AndIsNotTakenForContent()
    {
        var result = Edited(Lines("---", "  name: n", "  trigger: schedule"), Cron, null);

        Assert.Equal(Lines("---", "  name: n", "  trigger: schedule", "  schedule: \"0 30 1 * * *\""), result);
    }

    [Fact]
    public void NoTrailingNewline_IsKept_WhenTheKeyIsAddedLast()
    {
        var result = Edited("name: n\ntrigger: schedule", Cron, null);

        Assert.Equal("name: n\ntrigger: schedule\nschedule: \"0 30 1 * * *\"", result);
    }

    [Theory]
    [InlineData("schedule: |\n  0 0 0 * * *\n", "block, a flow")]
    [InlineData("schedule: [\"0 0 0 * * *\"]\n", "block, a flow")]
    [InlineData("schedule: &when \"0 0 0 * * *\"\n", "block, a flow")]
    [InlineData("schedule:\n  \"0 0 0 * * *\"\n", "continues on the following lines")]
    [InlineData("schedule: \"0 0 0\n  * * *\"\n", "several lines")]
    [InlineData("schedule: 0 0 0\n  * * *\n", "continues on the following lines")]
    [InlineData("schedule: \"0 0 0 * * *\" extra\n", "does not parse")]
    public void ValueLayoutTheEditorCannotKeep_IsRefused_NotGuessed(string scheduleLines, string reason)
    {
        var error = Refused("name: n\n" + scheduleLines + "stages: []\n", Cron, null);

        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0 0 0 * * *\" # x")]
    [InlineData("0 0 0 * * *\nstages: []")]
    [InlineData("0 \\ 0")]
    [InlineData("")]
    [InlineData(" 0 0 0 * * *")]
    public void ValueThatCannotBeWrittenSafely_IsRefused(string cron)
    {
        var error = Refused("name: n\nschedule: \"0 0 0 * * *\"\n", cron, null);

        Assert.Contains("cannot be written safely", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   \n", "empty")]
    [InlineData("- a\n- b\n", "mapping")]
    [InlineData("name: a\nstages: []\n---\nname: b\n", "does not parse")]
    public void EmptyNonMappingAndSeveralDocuments_AreRefused(string yaml, string reason)
    {
        Assert.Contains(reason, Refused(yaml, Cron, null), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTriggerAndNothingToChange_AreRefused()
    {
        Assert.Contains("must be", Refused("name: n\n", Cron, "webhook"), StringComparison.Ordinal);
        Assert.Contains("Nothing to change", Refused("name: n\n", null, null), StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedBlockRepeatingAKeyOfTheTopLevel_IsNotADuplicate()
    {
        var yaml = Lines("name: n", "variables:", "  A: 1", "stages:", "  - name: s", "    variables:", "      A: 2", "schedule: \"0 0 0 * * *\"");

        Assert.EndsWith("schedule: \"0 30 1 * * *\"\n", Edited(yaml, Cron, null), StringComparison.Ordinal);
    }

    [Fact]
    public void ResultAndOriginal_DifferOnlyByTheChangedKeys()
    {
        var yaml = Lines(
            "name: n", "trigger: manual", "variables:", "  A: '1'", "stages:", "  - name: build", "    steps:",
            "      - name: say", "        shell: echo \"hi\" # not a comment of the key", "schedule: \"0 0 0 * * *\" # keep");

        var before = YamlParsingHelper.ParseAndValidate(yaml)!;
        var after = YamlParsingHelper.ParseAndValidate(Edited(yaml, Cron, "schedule"))!;

        Assert.Equal(("schedule", Cron), (after.Trigger, after.Schedule));
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Variables, after.Variables);
        Assert.Equal(before.Stages.Count, after.Stages.Count);
        Assert.Equal(before.Stages[0].Steps[0].Shell, after.Stages[0].Steps[0].Shell);
    }
}
