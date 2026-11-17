// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>ADR-060: reads how far the run backfill went.</summary>
public interface IPipelineRunIdentityProgressService
{
    Task<PipelineRunIdentityBackfillDto> GetAsync(CancellationToken ct = default);
}

internal sealed class PipelineRunIdentityProgressService(IPipelineRunIdentityBackfillRepository repository)
    : IPipelineRunIdentityProgressService
{
    public Task<PipelineRunIdentityBackfillDto> GetAsync(CancellationToken ct = default) => repository.GetProgressAsync(ct);
}
