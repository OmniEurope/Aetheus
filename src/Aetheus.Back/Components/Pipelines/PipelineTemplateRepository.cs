// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineTemplateRepository(AppDbContext db) : IPipelineTemplateRepository
{
    /// <summary>The header filters of a template's version history grid.</summary>
    internal static readonly GridQueryMap<PipelineTemplateVersion> VersionColumns = new GridQueryMap<PipelineTemplateVersion>()
        .Number("Version", version => version.Version)
        .Text("ChangelogEntry", version => version.ChangelogEntry)
        .Text("CreatedByUsername", version => version.CreatedByUsername)
        .Date("CreatedAt", version => version.CreatedAt);

    public async Task<List<PipelineTemplate>> GetTemplatesAsync(CancellationToken ct = default) =>
        await db.PipelineTemplates.AsNoTracking()
            .OrderBy(template => template.Category)
            .ThenBy(template => template.Name)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<List<PipelineTemplateSummaryDto>> GetTemplateSummariesAsync(
        CancellationToken ct = default) =>
        await db.PipelineTemplates.AsNoTracking()
            .OrderBy(template => template.Name)
            .Select(template => new PipelineTemplateSummaryDto
            {
                Id = template.Id,
                Name = template.Name,
                Description = template.Description,
                Category = template.Category,
                Version = template.LatestVersion,
                OrganizationId = template.OrganizationId,
                UpdatedAt = template.UpdatedAt,
                PipelineCount = db.Pipelines.Count(pipeline =>
                    pipeline.TemplateReferenceName != null
                    && pipeline.TemplateReferenceName.ToLower() == template.Name.ToLower()
                    && (pipeline.Project != null
                            && pipeline.Project.OrganizationId == template.OrganizationId
                        || pipeline.Environment != null
                            && pipeline.Environment.Project != null
                            && pipeline.Environment.Project.OrganizationId == template.OrganizationId
                        || pipeline.ProjectServer != null
                            && pipeline.ProjectServer.Project.OrganizationId == template.OrganizationId)),
                LatestRunAt = db.PipelineRuns
                    .Where(run => run.Pipeline.TemplateReferenceName != null
                        && run.Pipeline.TemplateReferenceName.ToLower() == template.Name.ToLower()
                        && (run.Pipeline.Project != null
                                && run.Pipeline.Project.OrganizationId == template.OrganizationId
                            || run.Pipeline.Environment != null
                                && run.Pipeline.Environment.Project != null
                                && run.Pipeline.Environment.Project.OrganizationId == template.OrganizationId
                            || run.Pipeline.ProjectServer != null
                                && run.Pipeline.ProjectServer.Project.OrganizationId == template.OrganizationId))
                    .Max(run => (DateTime?)run.StartedAt)
            })
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<PipelineTemplate?> GetTemplateAsync(int id, CancellationToken ct = default) =>
        await db.PipelineTemplates.AsNoTracking()
            .FirstOrDefaultAsync(template => template.Id == id, ct).ConfigureAwait(false);

    public async Task<PipelineTemplate?> FindTemplateAsync(int id, CancellationToken ct = default) =>
        await db.PipelineTemplates
            .Include(template => template.Versions)
            .FirstOrDefaultAsync(template => template.Id == id, ct).ConfigureAwait(false);

    public async Task AddTemplateAsync(PipelineTemplate template, CancellationToken ct = default)
    {
        db.PipelineTemplates.Add(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveTemplateAsync(PipelineTemplate template, CancellationToken ct = default)
    {
        db.PipelineTemplates.Remove(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PipelineTemplate?> FindTemplateByNameAsync(
        string name, CancellationToken ct = default)
    {
        var normalizedName = name.ToLowerInvariant();
        return await db.PipelineTemplates.AsNoTracking()
            .FirstOrDefaultAsync(template => template.Name.ToLower() == normalizedName, ct)
            .ConfigureAwait(false);
    }

    public async Task<PipelineTemplate?> FindTemplateByNameAsync(
        string name, int organizationId, CancellationToken ct = default)
    {
        var normalizedName = name.ToLowerInvariant();
        return await db.PipelineTemplates.AsNoTracking()
            .Where(template => template.OrganizationId == organizationId)
            .FirstOrDefaultAsync(template => template.Name.ToLower() == normalizedName, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Shipped templates are shared from the platform organization to avoid divergent
    /// copies. Other platform templates remain private.</summary>
    public async Task<PipelineTemplate?> FindSharedTemplateByNameAsync(string name, CancellationToken ct = default)
    {
        var normalizedName = name.Trim().ToLowerInvariant();
        if (!DeliveryPipelineTemplateSeeder.Definitions.ContainsKey(normalizedName)) return null;
        return await db.PipelineTemplates.AsNoTracking()
            .Where(template => template.Organization.Slug == DeliveryPipelineTemplateSeeder.PlatformOrganizationSlug)
            .FirstOrDefaultAsync(template => template.Name.ToLower() == normalizedName, ct)
            .ConfigureAwait(false);
    }

    public async Task<PipelineTemplateVersion?> GetTemplateVersionAsync(
        int templateId, int version, CancellationToken ct = default) =>
        await db.PipelineTemplateVersions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.TemplateId == templateId && item.Version == version, ct)
            .ConfigureAwait(false);

    public async Task<(List<PipelineTemplateVersionSummaryDto> Items, int TotalCount)>
        GetTemplateVersionsPagedAsync(
            int templateId, int page, int pageSize, string? sortBy, bool sortDescending,
            CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.PipelineTemplateVersions.AsNoTracking()
            .Where(version => version.TemplateId == templateId);
        query = VersionColumns.ApplyFilters(query, columnFilters);
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("version", false) => query.OrderBy(version => version.Version),
            ("createdat", false) => query.OrderBy(version => version.CreatedAt).ThenBy(version => version.Version),
            ("createdat", true) => query.OrderByDescending(version => version.CreatedAt).ThenByDescending(version => version.Version),
            ("createdbyusername", false) => query.OrderBy(version => version.CreatedByUsername).ThenBy(version => version.Version),
            ("createdbyusername", true) => query.OrderByDescending(version => version.CreatedByUsername).ThenByDescending(version => version.Version),
            _ => query.OrderByDescending(version => version.Version)
        };
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(version => new PipelineTemplateVersionSummaryDto
            {
                Version = version.Version,
                ChangelogEntry = version.ChangelogEntry,
                CreatedAt = version.CreatedAt,
                CreatedByUsername = version.CreatedByUsername
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, totalCount);
    }

    public async Task<List<string>> GetPipelineYamlDefinitionsByOrganizationAsync(
        int organizationId, CancellationToken ct = default) =>
        await db.Pipelines.AsNoTracking()
            .Where(pipeline =>
                pipeline.Project != null && pipeline.Project.OrganizationId == organizationId
                || pipeline.Environment != null && pipeline.Environment.Project != null
                    && pipeline.Environment.Project.OrganizationId == organizationId
                || pipeline.ProjectServer != null
                    && pipeline.ProjectServer.Project.OrganizationId == organizationId)
            .Select(pipeline => pipeline.YamlDefinition)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<List<string>> GetTemplateVersionYamlDefinitionsByOrganizationAsync(
        int organizationId, int excludedTemplateId, CancellationToken ct = default) =>
        await db.PipelineTemplateVersions.AsNoTracking()
            .Where(version => version.Template.OrganizationId == organizationId
                && version.TemplateId != excludedTemplateId)
            .Select(version => version.YamlContent)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
