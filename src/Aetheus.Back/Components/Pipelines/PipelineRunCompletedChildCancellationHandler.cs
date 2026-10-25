// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// A run that ended Failed stops the runs its trigger steps started and that are still in flight
/// (<see cref="PipelineFailedParentChildCanceller"/>). The completion event is dispatched inline,
/// inside the failing parent's advance lock, and a cancelled child resolves its trigger step in the
/// parent under that same lock: cancelling inline would wait on a lock this very call holds. The work
/// is therefore moved onto the background queue, in its own scope, and runs once the lock is free.
/// </summary>
public sealed class PipelineRunCompletedChildCancellationHandler(IBackgroundTaskQueue queue)
    : IDomainEventHandler<PipelineRunCompletedEvent>
{
    public Task HandleAsync(PipelineRunCompletedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.Status != PipelineStatus.Failed)
            return Task.CompletedTask;

        queue.Enqueue((services, token) => services
            .GetRequiredService<PipelineFailedParentChildCanceller>()
            .CancelRunningChildrenAsync(domainEvent.PipelineRunId, token));
        return Task.CompletedTask;
    }
}
