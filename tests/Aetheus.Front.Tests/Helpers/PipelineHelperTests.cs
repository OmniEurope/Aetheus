// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests;

public class PipelineHelperTests
{
    [Theory]
    [InlineData(PipelineStatus.Success, OmniTone.Success)]
    [InlineData(PipelineStatus.Failed, OmniTone.Danger)]
    [InlineData(PipelineStatus.Running, OmniTone.Accent)]
    // Partial runs use amber to indicate a tolerated failure; cancelled runs use neutral grey.
    [InlineData(PipelineStatus.Cancelled, OmniTone.Info)]
    [InlineData(PipelineStatus.Partial, OmniTone.Warning)]
    [InlineData(PipelineStatus.RolledBack, OmniTone.Warning)]
    public void GetRunBadge_ReturnsCorrectStyle(PipelineStatus status, OmniTone expected)
    {
        Assert.Equal(expected, PipelineHelper.GetRunBadge(status));
    }

    [Fact]
    public void GetRunBadge_UnknownStatus_ReturnsLight()
    {
        Assert.Equal(OmniTone.Neutral, PipelineHelper.GetRunBadge((PipelineStatus)999));
    }

    [Theory]
    [InlineData(PipelineStatus.Running)]
    [InlineData(PipelineStatus.Pending)]
    [InlineData(PipelineStatus.WaitingForApproval)]
    public void GetCurrentStepLabel_ReturnsRunningOrNextPendingStep(PipelineStatus status)
    {
        var run = new PipelineRunDto
        {
            Status = status,
            Steps =
            [
                new PipelineStepRunDto { StageName = "Build", StepName = "Compile", Status = TaskExecutionStatus.Pending },
                new PipelineStepRunDto { StageName = "System:QA", StepName = "Tests", Status = TaskExecutionStatus.Running }
            ]
        };

        Assert.Equal("QA · Tests", PipelineHelper.GetCurrentStepLabel(run));
    }

    [Fact]
    public void GetCurrentStepLabel_ReturnsNullForTerminalRun()
    {
        var run = new PipelineRunDto
        {
            Status = PipelineStatus.Success,
            Steps = [new PipelineStepRunDto { StageName = "Build", StepName = "Compile", Status = TaskExecutionStatus.Success }]
        };

        Assert.Null(PipelineHelper.GetCurrentStepLabel(run));
    }

    [Fact]
    public void GetCurrentStepLabel_UsesAssignedStepBeforePendingStep()
    {
        var run = new PipelineRunDto
        {
            Status = PipelineStatus.Running,
            Steps =
            [
                new PipelineStepRunDto { StageName = "Build", StepName = "Pending", Status = TaskExecutionStatus.Pending },
                new PipelineStepRunDto { StageName = "QA", StepName = "Assigned", Status = TaskExecutionStatus.Assigned }
            ]
        };

        Assert.Equal("QA · Assigned", PipelineHelper.GetCurrentStepLabel(run));
    }
}
