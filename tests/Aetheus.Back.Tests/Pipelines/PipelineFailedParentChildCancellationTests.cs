// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-31: a failed parent run cancels the child runs its trigger steps started and that are still in
/// flight, so they end Cancelled ("Annulé") instead of running on and later reading as timeouts.
/// </summary>
public sealed class PipelineFailedParentChildCancellationTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRunService _runService = Substitute.For<IPipelineRunService>();

    private PipelineFailedParentChildCanceller Canceller() =>
        new(_repo, _runService, NullLogger<PipelineFailedParentChildCanceller>.Instance);

    [Fact]
    public async Task FailedParent_CancelsItsChildRunsThatAreStillInFlight()
    {
        _repo.GetTriggeredChildRunIdsAsync(10, Arg.Any<CancellationToken>()).Returns([11, 12, 13]);
        // 12 already ended: CancelRunAsync answers false for a terminal run.
        _runService.CancelRunAsync(11, Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>()).Returns(true);
        _runService.CancelRunAsync(12, Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>()).Returns(false);
        _runService.CancelRunAsync(13, Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>()).Returns(true);

        var cancelled = await Canceller().CancelRunningChildrenAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, cancelled);
        await _runService.Received(1).CancelRunAsync(
            11,
            Arg.Is<PipelineRunCancellation>(c => c.By == PipelineRunCancellation.SystemAuthor && c.Reason.Contains("#10")),
            Arg.Any<CancellationToken>());
        await _runService.Received(1).CancelRunAsync(13, Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedParentWithoutChildren_CancelsNothing()
    {
        _repo.GetTriggeredChildRunIdsAsync(10, Arg.Any<CancellationToken>()).Returns([]);

        var cancelled = await Canceller().CancelRunningChildrenAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(0, cancelled);
        await _runService.DidNotReceive().CancelRunAsync(
            Arg.Any<int>(), Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompletionEvent_OfAFailedRun_QueuesTheChildCancellation_AndOnlyThen()
    {
        _repo.GetTriggeredChildRunIdsAsync(10, Arg.Any<CancellationToken>()).Returns([11]);
        _runService.CancelRunAsync(11, Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>()).Returns(true);
        var queue = new BackgroundTaskQueue();
        var handler = new PipelineRunCompletedChildCancellationHandler(queue);
        var services = new ServiceCollection()
            .AddSingleton(Canceller())
            .BuildServiceProvider();

        // Not a failure: nothing is queued.
        foreach (var status in new[] { PipelineStatus.Success, PipelineStatus.Cancelled, PipelineStatus.Partial, PipelineStatus.Rejected })
            await handler.HandleAsync(new PipelineRunCompletedEvent(10, status), TestContext.Current.CancellationToken);
        Assert.Equal(0, queue.CurrentLength);

        await handler.HandleAsync(new PipelineRunCompletedEvent(10, PipelineStatus.Failed), TestContext.Current.CancellationToken);
        Assert.Equal(1, queue.CurrentLength);

        // Cancelling must not happen inline: the failing parent still holds its advance lock.
        await _runService.DidNotReceive().CancelRunAsync(
            Arg.Any<int>(), Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>());

        var work = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        await work(services, TestContext.Current.CancellationToken);

        await _runService.Received(1).CancelRunAsync(11, Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>());
    }
}
