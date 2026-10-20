// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Notifications;

namespace Aetheus.Front.Tests.Pages.Notifications;

/// <summary>Recette R3-16: the page a notification's detail offers to open, read from its payload.</summary>
public sealed class NotificationTargetsTests
{
    [Theory]
    [InlineData("ai-task.completed", """{"ProjectId":12,"AiTaskDefinitionId":5,"AiRunResultId":300}""", "/projects/12/ai-tasks/5?result=300")]
    [InlineData("ai-task.completed", """{"ProjectId":12,"AiTaskDefinitionId":5}""", "/projects/12/ai-tasks/5")]
    [InlineData("release.deployed", """{"ProjectId":1,"ReleaseId":8}""", "/releases/8")]
    [InlineData("pipeline.failed", """{"ProjectId":1,"PipelineRunId":2698}""", "/pipelines/runs/2698")]
    public void NamesThePageTheEventIsAbout(string eventType, string payload, string expected) =>
        Assert.Equal(expected, NotificationTargets.Path(eventType, payload));

    [Theory]
    [InlineData("ai-task.completed", """{"AiTaskDefinitionId":5}""")]
    [InlineData("app.down", """{"ProjectId":1}""")]
    [InlineData("pipeline.failed", "not json")]
    [InlineData("pipeline.failed", "")]
    public void HasNoTarget_WhenThePayloadDoesNotNameIt(string eventType, string payload) =>
        Assert.Null(NotificationTargets.Path(eventType, payload));
}
