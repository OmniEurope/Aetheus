// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using Aetheus.Front.Components.Notifications;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Aetheus.Front.Tests.Helpers;

/// <summary>Pure rules of three components, asserted without rendering them: the overview error lines
/// of a step, the gate violation sentence, and the Teamspeak dialog's required fields.</summary>
public sealed class FrontComponentRuleBranchTests
{
    private static TaskLogDto Log(int id, TaskLogLevel level) => new() { Id = id, Level = level, Message = $"line {id}" };

    private static PipelineStepRunDto Step(int? taskId) => new() { TaskId = taskId };

    [Fact]
    public void ErrorLinesFor_AStepWithoutTaskOrCachedLogs_HasNone()
    {
        var cache = new Dictionary<int, List<TaskLogDto>> { [1] = [], [2] = [Log(1, TaskLogLevel.Error)] };

        Assert.Empty(PipelineRunLogView.ErrorLinesFor(Step(null), cache));
        Assert.Empty(PipelineRunLogView.ErrorLinesFor(Step(9), cache));
        Assert.Empty(PipelineRunLogView.ErrorLinesFor(Step(1), cache));
    }

    [Fact]
    public void ErrorLinesFor_PrefersTheErrorLines()
    {
        var cache = new Dictionary<int, List<TaskLogDto>>
        {
            [1] = [Log(1, TaskLogLevel.Info), Log(2, TaskLogLevel.Error), Log(3, TaskLogLevel.Warning), Log(4, TaskLogLevel.Error)]
        };

        var lines = PipelineRunLogView.ErrorLinesFor(Step(1), cache);

        Assert.Equal([2, 4], lines.Select(line => line.Id));
    }

    [Fact]
    public void ErrorLinesFor_WithoutErrorLines_FallsBackToTheLastLines()
    {
        var logs = Enumerable.Range(1, 15).Select(id => Log(id, TaskLogLevel.Info)).ToList();
        var cache = new Dictionary<int, List<TaskLogDto>> { [1] = logs };

        var lines = PipelineRunLogView.ErrorLinesFor(Step(1), cache);

        Assert.Equal(Enumerable.Range(4, 12), lines.Select(line => line.Id));
    }

    [Fact]
    public void ErrorLinesFor_KeepsOnlyTheLastTwelveErrors()
    {
        var logs = Enumerable.Range(1, 20).Select(id => Log(id, TaskLogLevel.Error)).ToList();
        var cache = new Dictionary<int, List<TaskLogDto>> { [1] = logs };

        var lines = PipelineRunLogView.ErrorLinesFor(Step(1), cache);

        Assert.Equal(12, lines.Count);
        Assert.Equal(9, lines[0].Id);
        Assert.Equal(20, lines[^1].Id);
    }

    [Theory]
    [InlineData("GreaterThan", "8", "policy.key: finding = 40 (> 8)")]
    [InlineData("GreaterThanOrEqual", "8", "policy.key: finding = 40 (≥ 8)")]
    [InlineData("LessThan", "8", "policy.key: finding = 40 (< 8)")]
    [InlineData("LessThanOrEqual", "8", "policy.key: finding = 40 (≤ 8)")]
    [InlineData("Equal", "8", "policy.key: finding = 40 (= 8)")]
    [InlineData("Equals", "8", "policy.key: finding = 40 (= 8)")]
    [InlineData("Between", "8", "policy.key: finding = 40 (Between 8)")]
    [InlineData("GreaterThan", null, "policy.key: finding = 40")]
    [InlineData("GreaterThan", " ", "policy.key: finding = 40")]
    public void ViolationText_ReadsAsTheRuleAndItsThreshold(string op, string? threshold, string expected)
    {
        var violation = new AnalysisRunGateViolationDto
        {
            PolicyKey = "policy.key",
            TargetKind = "metric",
            TargetKey = "finding",
            ObservedValue = "40",
            Operator = op,
            Threshold = threshold,
        };

        Assert.Equal(expected, PipelineRunGateFindings.ViolationText(violation));
    }

    [Fact]
    public void ViolationText_AFindingTargetReadsAsItsRuleNotItsFingerprint()
    {
        var violation = new AnalysisRunGateViolationDto
        {
            PolicyKey = "quality.cycles",
            TargetKind = "Finding",
            TargetKey = "architecture.cycles.type:abc123",
            Operator = "GreaterThan",
            Threshold = "0",
        };

        Assert.Equal("quality.cycles: architecture.cycles.type = - (> 0)", PipelineRunGateFindings.ViolationText(violation));
    }

    [Fact]
    public void ViolationText_WithoutTargetKey_UsesTheTargetKind()
    {
        var violation = new AnalysisRunGateViolationDto { PolicyKey = "p", TargetKind = "run", ObservedValue = "1" };

        Assert.Equal("p: run = 1", PipelineRunGateFindings.ViolationText(violation));
    }

    [Theory]
    [InlineData("analysis.completed", "Quality")]
    [InlineData("app.down", "Monitoring")]
    [InlineData("alert.fired", "Monitoring")]
    [InlineData("release.deployed", "Releases")]
    [InlineData("pipeline.failed", "Pipelines")]
    [InlineData("agent-update.failed", "Servers")]
    [InlineData("backup.failed", "OtherEvents")]
    public void GroupKey_FollowsTheEventPrefix(string eventType, string group) =>
        Assert.Equal(group, NotificationPreferencesPanel.GroupKey(eventType));

    private static List<string> TeamspeakErrors(Action<TeamspeakDialogModel> configure)
    {
        var localizer = Substitute.For<IStringLocalizer>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var model = new TeamspeakDialogModel { L = localizer };
        configure(model);
        return [.. model.Validate(new ValidationContext(model))
            .Select(result => $"{string.Join(",", result.MemberNames)}:{result.ErrorMessage}")];
    }

    [Theory]
    [InlineData(TeamspeakDialogMode.Setup, "InstallPath")]
    [InlineData(TeamspeakDialogMode.Poke, "Message")]
    [InlineData(TeamspeakDialogMode.Message, "Message")]
    [InlineData(TeamspeakDialogMode.CreateChannel, "Name")]
    [InlineData(TeamspeakDialogMode.EditChannel, "Name")]
    [InlineData(TeamspeakDialogMode.SnapshotDeploy, "SnapshotBlob")]
    public void TeamspeakDialog_ARequiredFieldLeftBlank_IsReported(TeamspeakDialogMode mode, string field)
    {
        var errors = TeamspeakErrors(model =>
        {
            model.Mode = mode;
            model.InstallPath = " ";
            model.Message = " ";
            model.Name = " ";
            model.SnapshotBlob = " ";
            model.ExpectedConfirmation = "x";
            model.Confirmation = "x";
        });

        Assert.Equal([$"{field}:Required"], errors);
    }

    [Fact]
    public void TeamspeakDialog_ASnapshotDeployNeedsTheExactConfirmation()
    {
        var wrong = TeamspeakErrors(model =>
        {
            model.Mode = TeamspeakDialogMode.SnapshotDeploy;
            model.SnapshotBlob = "blob";
            model.ExpectedConfirmation = "DEPLOY";
            model.Confirmation = "deploy";
        });
        var right = TeamspeakErrors(model =>
        {
            model.Mode = TeamspeakDialogMode.SnapshotDeploy;
            model.SnapshotBlob = "blob";
            model.ExpectedConfirmation = "DEPLOY";
            model.Confirmation = "DEPLOY";
        });

        Assert.Equal(["Confirmation:SnapshotConfirmationMismatch"], wrong);
        Assert.Empty(right);
    }

    [Fact]
    public void TeamspeakDialog_OtherModesRequireNothing()
    {
        Assert.Empty(TeamspeakErrors(model => model.Mode = TeamspeakDialogMode.Kick));
    }

    [Fact]
    public void TeamspeakDialog_ValidatingWithoutALocalizer_Fails()
    {
        var model = new TeamspeakDialogModel { Mode = TeamspeakDialogMode.Setup, InstallPath = "" };

        Assert.Throws<InvalidOperationException>(() => model.Validate(new ValidationContext(model)).ToList());
    }
}
