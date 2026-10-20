// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>The queue positions of the root run and of each child run are read one run at a time; a run
/// whose queue cannot be read keeps what it had.</summary>
public sealed class PipelineRunQueueRefreshTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunQueueRefreshTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineRunDto Run(int id, int stepId, int? position = null) => new()
    {
        Id = id,
        Steps = [new PipelineStepRunDto { Id = stepId, Status = TaskExecutionStatus.Assigned, QueuePosition = position }]
    };

    private static PipelineRunQueueStateDto Queue(int runId, int stepId, int position) => new()
    {
        RunId = runId,
        Steps = [new PipelineStepQueueStateDto { StepId = stepId, Position = position, Depth = 5 }]
    };

    [Fact]
    public async Task TheRootAndEveryChild_GetTheirOwnQueuePositions()
    {
        _handler.SetJsonResponse("api/pipelines/runs/1/queue", Queue(1, 10, 2));
        _handler.SetJsonResponse("api/pipelines/runs/2/queue", Queue(2, 20, 4));
        var children = new Dictionary<int, PipelineRunDto> { [2] = Run(2, 20) };

        var root = await PipelineRunQueueRefresh.RefreshAsync(Services.GetRequiredService<ApiClient>(), Run(1, 10), children, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal((2, 5), (root.Steps[0].QueuePosition, root.Steps[0].QueueDepth));
        Assert.Equal((4, 5), (children[2].Steps[0].QueuePosition, children[2].Steps[0].QueueDepth));
    }

    [Fact]
    public async Task AQueueThatFailsOrIsEmpty_LeavesThatRunAsItWas()
    {
        _handler.SetResponse("api/pipelines/runs/1/queue", HttpStatusCode.InternalServerError);
        _handler.SetRawResponse("api/pipelines/runs/2/queue", "null");
        _handler.SetJsonResponse("api/pipelines/runs/3/queue", Queue(3, 30, 1));
        var original = Run(1, 10, position: 7);
        var child = Run(2, 20, position: 8);
        var children = new Dictionary<int, PipelineRunDto> { [2] = child, [3] = Run(3, 30) };

        var root = await PipelineRunQueueRefresh.RefreshAsync(Services.GetRequiredService<ApiClient>(), original, children, Xunit.TestContext.Current.CancellationToken);

        Assert.Same(original, root);
        Assert.Same(child, children[2]);
        Assert.Equal(1, children[3].Steps[0].QueuePosition);
    }
}
