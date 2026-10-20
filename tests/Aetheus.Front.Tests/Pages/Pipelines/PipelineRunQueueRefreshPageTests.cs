// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// The page's queue refresh: the queue position of the run's assigned steps is read again and the step
/// open in the logs follows it (its position and depth, or none once it left the queue), the opened
/// step staying the same.
/// </summary>
public sealed class PipelineRunQueueRefreshPageTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunQueueRefreshPageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/runs/90", new PipelineRunDto
        {
            Id = 90,
            PipelineId = 5,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Running,
            Steps =
            [
                new PipelineStepRunDto { Id = 11, StepName = "build", StageName = "Build", Status = TaskExecutionStatus.Assigned, ServerName = "runner-a" },
                new PipelineStepRunDto { Id = 12, StepName = "test", StageName = "Build", Status = TaskExecutionStatus.Pending }
            ]
        });
    }

    private IRenderedComponent<PipelineRun> RenderOnStep11()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/pipelines/runs/90?tab=logs&step=11");
        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 90));
        cut.WaitForAssertion(() => Assert.Equal(11, cut.Instance.SelectedStep?.Id));
        return cut;
    }

    [Fact]
    public async Task TheOpenedStep_TakesItsQueuePositionAndDepth()
    {
        var cut = RenderOnStep11();
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/runs/90/queue", new PipelineRunQueueStateDto
        {
            RunId = 90,
            Steps = [new PipelineStepQueueStateDto { StepId = 11, TaskId = 400, Position = 2, Depth = 5 }]
        });

        await cut.InvokeAsync(cut.Instance.RefreshQueueAsync);

        var selected = cut.Instance.SelectedStep!;
        Assert.Equal((11, 2, 5), (selected.Id, selected.QueuePosition, selected.QueueDepth));
        Assert.Contains(_handler.Requests, request => request.Method == "GET"
            && request.Url.EndsWith("api/pipelines/runs/90/queue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStepThatLeftTheQueue_LosesItsPosition()
    {
        var cut = RenderOnStep11();
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/runs/90/queue", new PipelineRunQueueStateDto
        {
            RunId = 90,
            Steps = [new PipelineStepQueueStateDto { StepId = 11, TaskId = 400, Position = 1, Depth = 1 }]
        });
        await cut.InvokeAsync(cut.Instance.RefreshQueueAsync);
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/runs/90/queue", new PipelineRunQueueStateDto { RunId = 90 });

        await cut.InvokeAsync(cut.Instance.RefreshQueueAsync);

        var selected = cut.Instance.SelectedStep!;
        Assert.Equal(11, selected.Id);
        Assert.Null(selected.QueuePosition);
        Assert.Null(selected.QueueDepth);
    }

    [Fact]
    public async Task AQueueThatCannotBeRead_LeavesTheOpenedStepAsItWas()
    {
        var cut = RenderOnStep11();
        _handler.SetResponse(HttpMethod.Get, "api/pipelines/runs/90/queue", System.Net.HttpStatusCode.ServiceUnavailable);

        await cut.InvokeAsync(cut.Instance.RefreshQueueAsync);

        var selected = cut.Instance.SelectedStep!;
        Assert.Equal(11, selected.Id);
        Assert.Null(selected.QueuePosition);
    }
}
