// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

public interface IPipelineFleetService
{
    Task<PaginatedResult<PipelineFleetItemDto>> GetAsync(
        PipelineFleetPaginationRequest request,
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds,
        CancellationToken ct = default);
    Task<PipelineFleetItemDto> GetItemAsync(int pipelineId, CancellationToken ct = default);

    /// <summary>The values the fleet's checkable column filters offer.</summary>
    Task<PipelineFleetFilterValuesDto> GetFilterValuesAsync(
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds,
        CancellationToken ct = default);
    Task<PipelineFleetUpdatePreviewDto> PreviewUpdateAsync(
        int pipelineId, int targetVersion, CancellationToken ct = default);
    Task<PipelineDto> UpdateAsync(
        int pipelineId, PipelineFleetUpdateRequest request, CancellationToken ct = default);
    Task<PipelineTemplateDto> ExtractAsync(
        int pipelineId, ExtractPipelineTemplateRequest request, CancellationToken ct = default);
    Task<PipelineTemplateDto> PromoteAsync(
        int pipelineId, PromotePipelineTemplateRequest request, CancellationToken ct = default);
    Task<PipelinePromotePreviewDto> PreviewPromotionAsync(
        int pipelineId, CancellationToken ct = default);
}
