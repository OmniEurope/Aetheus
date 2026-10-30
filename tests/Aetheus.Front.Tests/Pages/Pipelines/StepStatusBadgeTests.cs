// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Failure badges are red and filled (R-24); nonblocking failures explain that execution
/// continued.</summary>
public sealed class StepStatusBadgeTests : BunitContext
{
    public StepStatusBadgeTests() => BunitTestHelper.RegisterServices(this);

    private IRenderedComponent<StepStatusBadge> RenderFor(PipelineStepRunDto step) =>
        Render<StepStatusBadge>(parameters => parameters.Add(badge => badge.Step, step));

    [Fact]
    public void ANonBlockingFailure_IsRedFilled_AndSaysItDidNotBlock()
    {
        var badge = RenderFor(new PipelineStepRunDto { Status = TaskExecutionStatus.Failed, ContinueOnError = true })
            .Find(".omni-badge");

        Assert.Contains("omni-badge--danger", badge.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("omni-badge--outline", badge.ClassName, StringComparison.Ordinal);
        Assert.Equal("StepFailedNonBlocking", badge.GetAttribute("title"));
    }

    [Fact]
    public void ABlockingFailure_IsRedFilled_WithNoTooltip()
    {
        var badge = RenderFor(new PipelineStepRunDto { Status = TaskExecutionStatus.Failed })
            .Find(".omni-badge");

        Assert.Contains("omni-badge--danger", badge.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("omni-badge--outline", badge.ClassName, StringComparison.Ordinal);
        Assert.Null(badge.GetAttribute("title"));
    }

    [Fact]
    public void ARolledBackDeploy_KeepsItsWarningColour()
    {
        var badge = RenderFor(new PipelineStepRunDto
        {
            Status = TaskExecutionStatus.Failed,
            ContinueOnError = true,
            OutputVariables = new Dictionary<string, string> { ["DEPLOY_ROLLED_BACK"] = "true" }
        }).Find(".omni-badge");

        Assert.Contains("omni-badge--warning", badge.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void ARestoreThatFoundNoArtifact_ReadsSkipped_NotAGreenTick()
    {
        // The step completes Success because nothing failed, and restored nothing.
        var badge = RenderFor(new PipelineStepRunDto
        {
            Status = TaskExecutionStatus.Success,
            SkippedReason = "the selected release contract does not require this retained artifact"
        }).Find(".omni-badge");

        Assert.Contains("omni-badge--neutral", badge.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("omni-badge--success", badge.ClassName, StringComparison.Ordinal);
        Assert.Contains("StepSkippedArtifactMissing", badge.TextContent, StringComparison.Ordinal);
        Assert.Equal("the selected release contract does not require this retained artifact", badge.GetAttribute("title"));
    }

    [Fact]
    public void AStepSkippedByItsCondition_ReadsSkipped_WithAnIcon_AndTheCondition()
    {
        // Stored as Cancelled (the enum has no Skipped value), told apart by the condition.
        var badge = RenderFor(new PipelineStepRunDto
        {
            Status = TaskExecutionStatus.Cancelled,
            SkippedCondition = "eq(variables['HAS_PYTHON'], 'true')"
        }).Find(".omni-badge");

        Assert.Equal("Skipped", badge.TextContent.Trim());
        Assert.DoesNotContain("Enum_TaskExecutionStatus_Cancelled", badge.TextContent, StringComparison.Ordinal);
        Assert.NotNull(badge.QuerySelector(".omni-icon"));
        Assert.Equal("eq(variables['HAS_PYTHON'], 'true')", badge.GetAttribute("title"));
    }

    [Fact]
    public void ARealCancellation_StillReadsCancelled_WithoutTheSkippedIcon()
    {
        var badge = RenderFor(new PipelineStepRunDto { Status = TaskExecutionStatus.Cancelled }).Find(".omni-badge");

        Assert.Equal("Enum_TaskExecutionStatus_Cancelled", badge.TextContent.Trim());
        // A text badge always carries an icon (badge rule of 2026-10-25): the cancellation one, not the skipped one.
        Assert.Equal(OmniIconName.Prohibit, PipelineRunFormatting.StepBadgeIcon(new PipelineStepRunDto { Status = TaskExecutionStatus.Cancelled }));
        Assert.NotNull(badge.QuerySelector(".omni-icon"));
        Assert.Null(badge.GetAttribute("title"));
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Success)]
    [InlineData(TaskExecutionStatus.Failed)]
    public void AnAdvisoryStep_CarriesTheAdvisoryBadge_WhateverItsStatus(TaskExecutionStatus status)
    {
        var badges = RenderFor(new PipelineStepRunDto { Status = status, ContinueOnError = true, IsAdvisory = true })
            .FindAll(".omni-badge");

        Assert.Equal(2, badges.Count);
        Assert.Equal("StepAdvisory", badges[1].TextContent.Trim());
        Assert.Equal("StepAdvisoryHint", badges[1].GetAttribute("title"));
    }

    [Fact]
    public void AFailedAdvisoryStep_IsRedFilled_AndExplainsItIsAdvisory()
    {
        var badge = RenderFor(new PipelineStepRunDto
        {
            Status = TaskExecutionStatus.Failed,
            ContinueOnError = true,
            IsAdvisory = true
        }).Find(".omni-badge");

        Assert.Contains("omni-badge--danger", badge.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("omni-badge--outline", badge.ClassName, StringComparison.Ordinal);
        Assert.Equal("StepAdvisoryHint", badge.GetAttribute("title"));
    }

    [Fact]
    public void ANormalStep_HasNoAdvisoryBadge()
    {
        var cut = RenderFor(new PipelineStepRunDto { Status = TaskExecutionStatus.Failed, ContinueOnError = true });

        Assert.Single(cut.FindAll(".omni-badge"));
        Assert.DoesNotContain("StepAdvisory", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinarySuccess_StaysGreen()
    {
        var badge = RenderFor(new PipelineStepRunDto { Status = TaskExecutionStatus.Success }).Find(".omni-badge");

        Assert.Contains("omni-badge--success", badge.ClassName, StringComparison.Ordinal);
        // Seen on the local instance: the step list showed « SUCCÈS » without an icon after the badge rule.
        Assert.NotNull(badge.QuerySelector(".omni-icon"));
        Assert.Null(badge.GetAttribute("title"));
    }

    [Fact]
    public void R1_80_AControlThatRanAndFoundFailures_ReadsRejected_InWarning_NotSuccess()
    {
        var badge = RenderFor(new PipelineStepRunDto { Status = TaskExecutionStatus.Success, IsRejected = true })
            .Find(".omni-badge");

        Assert.Contains("omni-badge--warning", badge.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("omni-badge--success", badge.ClassName, StringComparison.Ordinal);
        Assert.Contains("Enum_PipelineStatus_Rejected", badge.TextContent, StringComparison.Ordinal);
        Assert.Equal("StepRejectedHint", badge.GetAttribute("title"));
    }

    [Fact]
    public void R1_80_AStage_IsRejectedOnlyWhenNothingInItFailedOrRuns()
    {
        var rejected = new PipelineStepRunDto { Status = TaskExecutionStatus.Success, IsRejected = true };
        var plain = new PipelineStepRunDto { Status = TaskExecutionStatus.Success };
        var failed = new PipelineStepRunDto { Status = TaskExecutionStatus.Failed };

        Assert.True(PipelineRunFormatting.IsRejectedStage([rejected, plain]));
        Assert.False(PipelineRunFormatting.IsRejectedStage([plain]));
        // A technical error outranks a rejection.
        Assert.False(PipelineRunFormatting.IsRejectedStage([rejected, failed]));
    }
}
