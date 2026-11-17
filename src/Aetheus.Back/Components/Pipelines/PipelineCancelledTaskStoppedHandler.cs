// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-12: the scheduler holds a run's cleanup back while an agent is still killing the process tree of a
/// cancelled task. When that agent reports the stop, this re-drives the run so the held-back stages go out.
/// </summary>
internal sealed class PipelineCancelledTaskStoppedHandler(IPipelineRunService runService)
    : IDomainEventHandler<PipelineCancelledTaskStoppedEvent>
{
    public Task HandleAsync(PipelineCancelledTaskStoppedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return runService.ContinueAfterCancelledTaskStoppedAsync(domainEvent.PipelineRunId, ct);
    }
}
