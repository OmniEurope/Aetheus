// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Persists the expand/contract verdicts a completed migrate step reported (recette R1-43).
/// Tasks parses the directive because it owns the log; this side owns the run and therefore the write.
/// </summary>
internal sealed class PipelineStepMigrationGateHandler(IPipelineMigrationGateRepository repo)
    : IDomainEventHandler<PipelineStepMigrationGateReportedEvent>
{
    public async Task HandleAsync(PipelineStepMigrationGateReportedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.Verdicts.Count == 0) return;

        await repo.AddAsync(domainEvent.Verdicts, ct).ConfigureAwait(false);
    }
}
