// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

internal interface IPipelineFleetRepository
{
    Task<PaginatedResult<PipelineFleetItemDto>> GetPageAsync(
        PipelineFleetPaginationRequest request,
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds,
        CancellationToken ct);

    Task<PipelineFleetRow?> GetAsync(int pipelineId, CancellationToken ct);

    /// <summary>The distinct template names the pipelines in scope reference.</summary>
    Task<List<string>> GetTemplateNamesAsync(
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds,
        CancellationToken ct);
}
