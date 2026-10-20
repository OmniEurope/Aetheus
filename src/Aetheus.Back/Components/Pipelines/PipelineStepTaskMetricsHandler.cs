// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Persists the pipeline telemetry a completed task's log carried.
///
/// Tasks parses the markers because it owns the log; this side owns the run and therefore the write.
/// The dispatch is strict: metrics dropped by a caught-and-logged handler would make a run page
/// under-report while every step still reported success.
/// </summary>
internal sealed class PipelineStepTaskMetricsHandler(IPipelineQualityResultRepository repo)
    : IDomainEventHandler<PipelineStepTaskMetricsCollectedEvent>
{
    public async Task HandleAsync(
        PipelineStepTaskMetricsCollectedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.Metrics.Count == 0) return;

        await repo.AddRunMetricsAsync(domainEvent.Metrics, ct).ConfigureAwait(false);
    }
}
