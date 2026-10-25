// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// A call-scoped decorator that remembers the three lookups the template resolver repeats for every
/// pipeline extending the same template (by name and organization, shared by name, a template version),
/// null answers included, so one request reads each distinct row once. Every other member passes through.
/// Not thread-safe and never shared: create one per request.
/// </summary>
public sealed class MemoizingPipelineTemplateRepository(IPipelineTemplateRepository inner) : IPipelineTemplateRepository
{
    private readonly Dictionary<(string Name, int OrganizationId), PipelineTemplate?> _byName = [];
    private readonly Dictionary<string, PipelineTemplate?> _shared = [];
    private readonly Dictionary<(int TemplateId, int Version), PipelineTemplateVersion?> _versions = [];

    public async Task<PipelineTemplate?> FindTemplateByNameAsync(string name, int organizationId, CancellationToken ct = default)
    {
        var key = (name, organizationId);
        if (_byName.TryGetValue(key, out var cached)) return cached;
        return _byName[key] = await inner.FindTemplateByNameAsync(name, organizationId, ct).ConfigureAwait(false);
    }

    public async Task<PipelineTemplate?> FindSharedTemplateByNameAsync(string name, CancellationToken ct = default)
    {
        if (_shared.TryGetValue(name, out var cached)) return cached;
        return _shared[name] = await inner.FindSharedTemplateByNameAsync(name, ct).ConfigureAwait(false);
    }

    public async Task<PipelineTemplateVersion?> GetTemplateVersionAsync(int templateId, int version, CancellationToken ct = default)
    {
        var key = (templateId, version);
        if (_versions.TryGetValue(key, out var cached)) return cached;
        return _versions[key] = await inner.GetTemplateVersionAsync(templateId, version, ct).ConfigureAwait(false);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => inner.SaveChangesAsync(ct);

    public Task<List<PipelineTemplate>> GetTemplatesAsync(CancellationToken ct = default) => inner.GetTemplatesAsync(ct);

    public Task<List<PipelineTemplateSummaryDto>> GetTemplateSummariesAsync(CancellationToken ct = default) =>
        inner.GetTemplateSummariesAsync(ct);

    public Task<PipelineTemplate?> GetTemplateAsync(int id, CancellationToken ct = default) => inner.GetTemplateAsync(id, ct);

    public Task<PipelineTemplate?> FindTemplateAsync(int id, CancellationToken ct = default) => inner.FindTemplateAsync(id, ct);

    public Task AddTemplateAsync(PipelineTemplate template, CancellationToken ct = default) => inner.AddTemplateAsync(template, ct);

    public Task RemoveTemplateAsync(PipelineTemplate template, CancellationToken ct = default) => inner.RemoveTemplateAsync(template, ct);

    public Task<PipelineTemplate?> FindTemplateByNameAsync(string name, CancellationToken ct = default) =>
        inner.FindTemplateByNameAsync(name, ct);

    public Task<(List<PipelineTemplateVersionSummaryDto> Items, int TotalCount)> GetTemplateVersionsPagedAsync(
        int templateId, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null) =>
        inner.GetTemplateVersionsPagedAsync(templateId, page, pageSize, sortBy, sortDescending, ct, columnFilters);

    public Task<List<string>> GetPipelineYamlDefinitionsByOrganizationAsync(int organizationId, CancellationToken ct = default) =>
        inner.GetPipelineYamlDefinitionsByOrganizationAsync(organizationId, ct);

    public Task<List<string>> GetTemplateVersionYamlDefinitionsByOrganizationAsync(
        int organizationId, int excludedTemplateId, CancellationToken ct = default) =>
        inner.GetTemplateVersionYamlDefinitionsByOrganizationAsync(organizationId, excludedTemplateId, ct);
}
