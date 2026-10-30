// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

public interface IPipelineTemplateService
{
    Task<List<PipelineTemplateSummaryDto>> GetTemplatesAsync(CancellationToken ct = default);
    Task<PipelineTemplateDto?> GetTemplateAsync(int id, CancellationToken ct = default);
    Task<PaginatedResult<PipelineTemplateVersionSummaryDto>> GetTemplateVersionsAsync(
        int id, PaginationRequest request, CancellationToken ct = default);
    Task<PipelineTemplateVersionDto?> GetTemplateVersionAsync(
        int id, int version, CancellationToken ct = default);
    Task<PipelineTemplateDto> CreateTemplateAsync(CreatePipelineTemplateRequest request, CancellationToken ct = default);
    Task<PipelineTemplateDto?> UpdateTemplateAsync(int id, UpdatePipelineTemplateRequest request, CancellationToken ct = default);
    Task<bool> DeleteTemplateAsync(int id, CancellationToken ct = default);
    Task<PipelineTemplateDto?> ImportTemplateAsync(
        string yamlContent, CancellationToken ct = default, int? organizationId = null);
    /// <summary>True for a delivery template the platform ships and shares with every organization
    /// (catalog name held by the platform organization): only a site Admin may change it.</summary>
    Task<bool> IsSharedCatalogTemplateAsync(int id, CancellationToken ct = default);
    /// <summary>True when a template of this name in this organization would be such a shared
    /// catalog template.</summary>
    Task<bool> IsSharedCatalogNameAsync(string name, int organizationId, CancellationToken ct = default);
    Task<string?> ResolveTemplateAsync(
        string yamlContent,
        Dictionary<string, string>? parameters = null,
        CancellationToken ct = default,
        int? organizationId = null);
}
