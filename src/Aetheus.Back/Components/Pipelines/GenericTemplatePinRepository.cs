// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;

namespace Aetheus.Back.Components.Pipelines;

internal sealed class GenericTemplatePinRepository(AppDbContext db) : IGenericTemplatePinRepository
{
    public async Task<GenericTemplatePinSnapshot?> LoadAsync(
        IReadOnlyCollection<string> templateNames, GenericTemplatePinScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var platformOrganizationId = await db.Organizations.AsNoTracking()
            .Where(organization => organization.Slug == DeliveryTemplateCatalog.PlatformOrganizationSlug)
            .Select(organization => (int?)organization.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (platformOrganizationId is not { } platformId) return null;

        var names = templateNames.ToArray();
        var catalog = await db.PipelineTemplates.AsNoTracking()
            .Where(template => template.OrganizationId == platformId && names.Contains(template.Name))
            .Select(template => new { template.Name, template.LatestVersion })
            .ToDictionaryAsync(template => template.Name, template => template.LatestVersion, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var pipelines = db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.TemplateReferenceName != null
                && pipeline.TemplateReferenceVersion != null
                && names.Contains(pipeline.TemplateReferenceName));
        if (scope.AccessiblePipelineIds is { } accessible)
            pipelines = pipelines.Where(pipeline => accessible.Contains(pipeline.Id));
        if (scope.OrganizationIds is { } organizationIds)
            pipelines = pipelines.InOrganizations(organizationIds);
        if (scope.ProjectId is { } projectId)
            pipelines = pipelines.Where(pipeline =>
                (pipeline.ProjectId ?? pipeline.Environment!.ProjectId ?? pipeline.ProjectServer!.ProjectId) == projectId);
        var candidates = await pipelines
            .OrderBy(pipeline => pipeline.Name).ThenBy(pipeline => pipeline.Id)
            .Select(pipeline => new GenericTemplatePinCandidate(
                pipeline.Id,
                pipeline.Name,
                pipeline.TemplateReferenceName!,
                pipeline.TemplateReferenceVersion!.Value,
                pipeline.Project != null
                    ? pipeline.Project.OrganizationId
                    : pipeline.Environment != null && pipeline.Environment.Project != null
                        ? pipeline.Environment.Project.OrganizationId
                        : pipeline.ProjectServer != null ? pipeline.ProjectServer.Project.OrganizationId : 0,
                pipeline.ExternalImportCommit != null))
            .ToListAsync(ct).ConfigureAwait(false);

        var owned = await db.PipelineTemplates.AsNoTracking()
            .Where(template => template.OrganizationId != platformId && names.Contains(template.Name))
            .Select(template => new { template.OrganizationId, template.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        return new GenericTemplatePinSnapshot(
            catalog,
            candidates,
            owned.Select(template => (template.OrganizationId, template.Name)).ToHashSet());
    }
}
