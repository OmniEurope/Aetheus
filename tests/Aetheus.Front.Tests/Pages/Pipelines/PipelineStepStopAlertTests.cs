// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R-31: a step that failed, timed out or was cancelled says why. A "Délai dépassé" step used to
/// show nothing. The test localizer renders keys, so the assertions read resource keys.
/// </summary>
public sealed class PipelineStepStopAlertTests : BunitContext
{
    public PipelineStepStopAlertTests() => BunitTestHelper.RegisterServices(this, isAdmin: true);

    private IRenderedComponent<PipelineStepStopAlert> Render(PipelineStepRunDto step) =>
        Render<PipelineStepStopAlert>(parameters => parameters.Add(alert => alert.Step, step));

    private static PipelineStepRunDto Step(TaskExecutionStatus status, string? code = null, string? reason = null) => new()
    {
        Id = 1,
        StepName = "History scan",
        StageName = "Security",
        Status = status,
        FailureCode = code,
        FailureReason = reason
    };

    [Fact]
    public void ASupersededAgentSession_IsSaidInPlainWords_WithTheRawReasonFolded()
    {
        var cut = Render(Step(
            TaskExecutionStatus.Failed,
            TaskFailureCodes.AgentSessionReplaced,
            "Agent session replaced while the task was running; not replayed automatically."));

        Assert.Contains("StepStopAgentSessionReplaced", cut.Find("[data-testid=step-stop-reason]").TextContent, StringComparison.Ordinal);
        var detail = cut.FindComponent<OmniFieldset>();
        Assert.True(detail.Instance.Collapsible);
        Assert.False(detail.Instance.Expanded);
        Assert.Contains("Agent session replaced while the task was running", detail.Markup, StringComparison.Ordinal);
    }

    /// <summary>R-14: the alert and its folded detail carry no margin; the headline and the detail are spaced by
    /// the gap of the stack that holds them, which is how the container, not the block, owns the space.</summary>
    [Fact]
    public void TheAlertAndItsDetail_CarryNoMargin_TheirStackSpacesThem()
    {
        var cut = Render(Step(TaskExecutionStatus.Failed, TaskFailureCodes.ToolError, "docker: exit status 125"));

        var alert = cut.Find("[data-testid=step-stop-reason]");
        var detail = cut.Find("[data-testid=step-stop-detail]");
        Assert.DoesNotContain("omni-u-m", alert.ClassName ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("omni-u-m", detail.ClassName ?? string.Empty, StringComparison.Ordinal);
        var stack = detail.ParentElement!;
        Assert.Contains("omni-stack", stack.ClassName, StringComparison.Ordinal);
        Assert.Contains("omni-stack--gap-md", stack.ClassName, StringComparison.Ordinal);
        Assert.Contains("step-stop-detail-text", detail.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void ATimedOutStepWithNoRecordedReason_StillSaysItExceededItsLimit()
    {
        var cut = Render(Step(TaskExecutionStatus.Timeout));

        var alert = cut.FindComponent<OmniAlert>();
        Assert.Equal(OmniSeverity.Warning, alert.Instance.Severity);
        Assert.Contains("StepStopTimeout", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindComponents<OmniFieldset>());
    }

    [Theory]
    [InlineData(TaskFailureCodes.InfrastructureMismatch, "StepStopInfrastructureMismatch")]
    [InlineData(TaskFailureCodes.ToolError, "StepStopToolError")]
    [InlineData(TaskFailureCodes.BuildFailed, "StepStopBuildFailed")]
    [InlineData(TaskFailureCodes.TestsFailed, "StepStopTestsFailed")]
    [InlineData(TaskFailureCodes.AgentIncompatible, "StepStopAgentIncompatible")]
    public void AKnownFailureCode_IsSaidInPlainWords_NotAsItsRawCode(string code, string expectedKey)
    {
        var cut = Render(Step(TaskExecutionStatus.Failed, code, "docker: exit status 125"));

        var text = cut.Find("[data-testid=step-stop-reason]").TextContent;
        Assert.Contains(expectedKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(code, text.Replace(expectedKey, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("docker: exit status 125", cut.Find("[data-testid=step-stop-detail]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedStepWithAnUnknownCode_AndAnExitCode_SaysTheExitCode()
    {
        var cut = Render(Step(TaskExecutionStatus.Failed, "SomethingNew") with { ExitCode = 137 });

        Assert.Contains("StepStopFailedExit", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ACancelledStep_SaysItWasCancelled_AndASkippedOneSaysItsConditionWasNotMet()
    {
        Assert.Contains("StepStopCancelled", Render(Step(TaskExecutionStatus.Cancelled)).Markup, StringComparison.Ordinal);

        var skipped = Render(Step(TaskExecutionStatus.Cancelled) with { SkippedCondition = "eq(var, 'x')" });
        Assert.Contains("StepStopConditionNotMet", skipped.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Success)]
    [InlineData(TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.Pending)]
    public void AStepThatDidNotStop_ShowsNothing(TaskExecutionStatus status)
    {
        var cut = Render(Step(status));

        Assert.Empty(cut.FindComponents<OmniAlert>());
    }
}
