// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary>The connector after a stage takes the next stage's colour once the stage succeeded and the
/// next one started, a dedicated colour when it then failed, and the stage's own colour otherwise.</summary>
public sealed class PipelineRunConnectorStatusTests
{
    private static List<StageViewModel> Stages(params TaskExecutionStatus[] statuses) =>
        statuses.Select((status, index) => new StageViewModel { Name = $"s{index}", Status = status }).ToList();

    [Theory]
    [InlineData(TaskExecutionStatus.Success, TaskExecutionStatus.Failed, "success-failed")]
    [InlineData(TaskExecutionStatus.Success, TaskExecutionStatus.Running, "running")]
    [InlineData(TaskExecutionStatus.Success, TaskExecutionStatus.Pending, "success")]
    [InlineData(TaskExecutionStatus.Failed, TaskExecutionStatus.Pending, "failed")]
    [InlineData(TaskExecutionStatus.Running, TaskExecutionStatus.Failed, "running")]
    public void TheConnector_FollowsBothStages(TaskExecutionStatus current, TaskExecutionStatus next, string expected) =>
        Assert.Equal(expected, PipelineRunPresentation.NextConnectorStatus(Stages(current, next), 0));

    [Fact]
    public void AfterTheLastStage_TheConnectorIsPending() =>
        Assert.Equal("pending", PipelineRunPresentation.NextConnectorStatus(Stages(TaskExecutionStatus.Success), 0));
}
