// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Pipeline template catalogue: templates, their versions, and the organization YAML read before a
/// template is deleted. The port of the template service, the template resolver and the fleet service,
/// which need nothing else of the pipeline data (audit AUD-07). <see cref="IPipelineRepository"/>
/// composes it and delegates to the same queries.
/// </summary>
public interface IPipelineTemplateRepository : IPipelineUnitOfWork
{
    Task<List<PipelineTemplate>> GetTemplatesAsync(CancellationToken ct = default);

    Task<List<PipelineTemplateSummaryDto>> GetTemplateSummariesAsync(CancellationToken ct = default);

    Task<PipelineTemplate?> GetTemplateAsync(int id, CancellationToken ct = default);

    Task<PipelineTemplate?> FindTemplateAsync(int id, CancellationToken ct = default);

    Task AddTemplateAsync(PipelineTemplate template, CancellationToken ct = default);

    Task RemoveTemplateAsync(PipelineTemplate template, CancellationToken ct = default);

    Task<PipelineTemplate?> FindTemplateByNameAsync(string name, CancellationToken ct = default);

    Task<PipelineTemplate?> FindTemplateByNameAsync(string name, int organizationId, CancellationToken ct = default);

    /// <summary>A delivery template the platform ships, as the platform organization holds it.
    /// Every organization may extend it; null for any other name.</summary>
    Task<PipelineTemplate?> FindSharedTemplateByNameAsync(string name, CancellationToken ct = default);

    Task<PipelineTemplateVersion?> GetTemplateVersionAsync(int templateId, int version, CancellationToken ct = default);

    Task<(List<PipelineTemplateVersionSummaryDto> Items, int TotalCount)> GetTemplateVersionsPagedAsync(
        int templateId, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null);

    Task<List<string>> GetPipelineYamlDefinitionsByOrganizationAsync(
        int organizationId, CancellationToken ct = default);
    Task<List<string>> GetTemplateVersionYamlDefinitionsByOrganizationAsync(
        int organizationId, int excludedTemplateId, CancellationToken ct = default);
}
