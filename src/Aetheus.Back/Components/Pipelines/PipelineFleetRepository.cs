// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineFleetRepository(AppDbContext db) : IPipelineFleetRepository
{
    public async Task<PaginatedResult<PipelineFleetItemDto>> GetPageAsync(
        PipelineFleetPaginationRequest request,
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds,
        CancellationToken ct)
    {
        var pipelines = FilterPipelines(
            db.Pipelines.AsNoTracking(), request, organizationIds, accessiblePipelineIds);
        var fleet = BuildFleetQuery(pipelines);
        if (request.TemplateId.HasValue)
            fleet = fleet.Where(item => item.TemplateId == request.TemplateId.Value);
        if (request.Freshness.HasValue)
            fleet = fleet.Where(item => item.Freshness == request.Freshness.Value);
        // The grid's column header filters, after the scope and before the count.
        fleet = PipelineFleetQuery.Columns.ApplyFilters(fleet, request.Filters);
        fleet = Sort(fleet, request.SortBy, request.SortDescending);
        var totalCount = await fleet.CountAsync(ct).ConfigureAwait(false);
        var (page, pageSize) = request.Normalize();
        var items = await fleet.Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return new PaginatedResult<PipelineFleetItemDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    private static IQueryable<Data.Entities.Pipeline> FilterPipelines(
        IQueryable<Data.Entities.Pipeline> pipelines,
        PipelineFleetPaginationRequest request,
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds)
    {
        if (accessiblePipelineIds is not null)
            pipelines = pipelines.Where(pipeline => accessiblePipelineIds.Contains(pipeline.Id));
        if (organizationIds is not null)
            pipelines = pipelines.Where(pipeline => organizationIds.Contains(
                pipeline.Project != null
                    ? pipeline.Project.OrganizationId
                    : pipeline.Environment != null && pipeline.Environment.Project != null
                        ? pipeline.Environment.Project.OrganizationId
                        : pipeline.ProjectServer != null ? pipeline.ProjectServer.Project.OrganizationId : 0));
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            pipelines = pipelines.Where(pipeline => pipeline.Name.Contains(search)
                || pipeline.TemplateReferenceName != null && pipeline.TemplateReferenceName.Contains(search)
                || pipeline.Project != null && pipeline.Project.Name.Contains(search)
                || pipeline.Environment != null && pipeline.Environment.Name.Contains(search)
                || pipeline.ProjectServer != null && pipeline.ProjectServer.DisplayName.Contains(search));
        }
        if (request.ProjectId is { } requestedProjectId)
            pipelines = pipelines.Where(pipeline =>
                (pipeline.ProjectId ?? pipeline.Environment!.ProjectId ?? pipeline.ProjectServer!.ProjectId)
                == requestedProjectId);
        return pipelines;
    }

    private IQueryable<PipelineFleetItemDto> BuildFleetQuery(
        IQueryable<Data.Entities.Pipeline> pipelines) =>
            from ownership in ProjectOwnership(pipelines)
            join template in db.PipelineTemplates.AsNoTracking()
                on new
                {
                    ownership.OrganizationId,
                    Name = ownership.TemplateReferenceName == null
                        ? null
                        : ownership.TemplateReferenceName.ToLower()
                }
                equals new
                {
                    template.OrganizationId,
                    Name = (string?)template.Name.ToLower()
                }
                into templateMatches
            from template in templateMatches.DefaultIfEmpty()
            select new PipelineFleetItemDto
            {
                PipelineId = ownership.PipelineId,
                PipelineName = ownership.PipelineName,
                ProjectId = ownership.ProjectId,
                OwnerName = ownership.OwnerName,
                OwnerType = ownership.OwnerType,
                OrganizationId = ownership.OrganizationId,
                TemplateId = template == null ? null : template.Id,
                TemplateName = ownership.TemplateReferenceName,
                PinnedVersion = ownership.TemplateReferenceVersion,
                LatestVersion = template == null ? null : template.LatestVersion,
                UsesLegacyReference =
                    ownership.TemplateReferenceName != null && ownership.TemplateReferenceVersion == null,
                Freshness = ownership.TemplateReferenceName == null
                    ? PipelineFleetFreshness.OffCatalog
                    : template != null
                        && (ownership.TemplateReferenceVersion ?? template.LatestVersion) == template.LatestVersion
                            ? PipelineFleetFreshness.Current
                            : PipelineFleetFreshness.Outdated
            };

    private static IQueryable<PipelineFleetOwnershipRow> ProjectOwnership(
        IQueryable<Data.Entities.Pipeline> pipelines) =>
        ProjectOwned(pipelines)
            .Concat(EnvironmentOwned(pipelines))
            .Concat(ProjectServerOwned(pipelines))
            .Concat(Unowned(pipelines));

    private static IQueryable<PipelineFleetOwnershipRow> ProjectOwned(
        IQueryable<Data.Entities.Pipeline> pipelines) =>
        pipelines.Where(pipeline => pipeline.ProjectId != null)
            .Select(pipeline => new PipelineFleetOwnershipRow
            {
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                ProjectId = pipeline.ProjectId,
                TemplateReferenceName = pipeline.TemplateReferenceName,
                TemplateReferenceVersion = pipeline.TemplateReferenceVersion,
                OwnerName = pipeline.Project!.Name,
                OwnerType = "Project",
                OrganizationId = pipeline.Project.OrganizationId
            });

    private static IQueryable<PipelineFleetOwnershipRow> EnvironmentOwned(
        IQueryable<Data.Entities.Pipeline> pipelines) =>
        pipelines.Where(pipeline => pipeline.ProjectId == null && pipeline.EnvironmentId != null)
            .Select(pipeline => new PipelineFleetOwnershipRow
            {
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                ProjectId = pipeline.ProjectId,
                TemplateReferenceName = pipeline.TemplateReferenceName,
                TemplateReferenceVersion = pipeline.TemplateReferenceVersion,
                OwnerName = pipeline.Environment!.Name,
                OwnerType = "Environment",
                OrganizationId = pipeline.Environment.Project!.OrganizationId
            });

    private static IQueryable<PipelineFleetOwnershipRow> ProjectServerOwned(
        IQueryable<Data.Entities.Pipeline> pipelines) =>
        pipelines.Where(pipeline => pipeline.ProjectId == null
                && pipeline.EnvironmentId == null && pipeline.ProjectServerId != null)
            .Select(pipeline => new PipelineFleetOwnershipRow
            {
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                ProjectId = pipeline.ProjectId,
                TemplateReferenceName = pipeline.TemplateReferenceName,
                TemplateReferenceVersion = pipeline.TemplateReferenceVersion,
                OwnerName = pipeline.ProjectServer!.DisplayName,
                OwnerType = "ProjectServer",
                OrganizationId = pipeline.ProjectServer.Project.OrganizationId
            });

    private static IQueryable<PipelineFleetOwnershipRow> Unowned(
        IQueryable<Data.Entities.Pipeline> pipelines) =>
        pipelines.Where(pipeline => pipeline.ProjectId == null
                && pipeline.EnvironmentId == null && pipeline.ProjectServerId == null)
            .Select(pipeline => new PipelineFleetOwnershipRow
            {
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                ProjectId = pipeline.ProjectId,
                TemplateReferenceName = pipeline.TemplateReferenceName,
                TemplateReferenceVersion = pipeline.TemplateReferenceVersion,
                OwnerName = "Unowned",
                OwnerType = "ProjectServer",
                OrganizationId = 0
            });

    public Task<List<string>> GetTemplateNamesAsync(
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds,
        CancellationToken ct) =>
        FilterPipelines(db.Pipelines.AsNoTracking(), new PipelineFleetPaginationRequest(), organizationIds, accessiblePipelineIds)
            .Where(pipeline => pipeline.TemplateReferenceName != null && pipeline.TemplateReferenceName != "")
            .Select(pipeline => pipeline.TemplateReferenceName!)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(ct);

    public Task<PipelineFleetRow?> GetAsync(int pipelineId, CancellationToken ct) =>
        Project(db.Pipelines.AsNoTracking().Where(pipeline => pipeline.Id == pipelineId))
            .FirstOrDefaultAsync(ct);

    private static IQueryable<PipelineFleetRow> Project(
        IQueryable<Data.Entities.Pipeline> pipelines) =>
        pipelines.Select(pipeline => new PipelineFleetRow(
            pipeline.Id,
            pipeline.Name,
            pipeline.Description,
            pipeline.YamlDefinition,
            pipeline.SourceBranch,
            pipeline.ProjectId,
            pipeline.ProjectId ?? pipeline.Environment!.ProjectId ?? pipeline.ProjectServer!.ProjectId,
            pipeline.EnvironmentId,
            pipeline.ProjectServerId,
            pipeline.Project != null
                ? pipeline.Project.Name
                : pipeline.Environment != null
                    ? pipeline.Environment.Name
                    : pipeline.ProjectServer != null ? pipeline.ProjectServer.DisplayName : "Unowned",
            pipeline.Project != null ? "Project" : pipeline.Environment != null ? "Environment" : "ProjectServer",
            pipeline.Project != null
                ? pipeline.Project.OrganizationId
                : pipeline.Environment != null && pipeline.Environment.Project != null
                    ? pipeline.Environment.Project.OrganizationId
                    : pipeline.ProjectServer != null ? pipeline.ProjectServer.Project.OrganizationId : 0)
        {
            AiRunnerProfileId = pipeline.AiRunnerProfileId
        });

    private static IQueryable<PipelineFleetItemDto> Sort(
        IQueryable<PipelineFleetItemDto> items,
        string? sortBy,
        bool descending)
    {
        var key = sortBy?.Trim().ToLowerInvariant();
        return (key, descending) switch
        {
            ("pipelinename", false) => items.OrderBy(item => item.PipelineName.ToLower())
                .ThenBy(item => item.PipelineName).ThenBy(item => item.PipelineId),
            ("pipelinename", true) => items.OrderByDescending(item => item.PipelineName.ToLower())
                .ThenByDescending(item => item.PipelineName).ThenBy(item => item.PipelineId),
            ("ownername", false) => items.OrderBy(item => item.OwnerName.ToLower())
                .ThenBy(item => item.OwnerName).ThenBy(item => item.PipelineId),
            ("ownername", true) => items.OrderByDescending(item => item.OwnerName.ToLower())
                .ThenByDescending(item => item.OwnerName).ThenBy(item => item.PipelineId),
            ("templatename", false) => items.OrderBy(item => item.TemplateName == null)
                .ThenBy(item => item.TemplateName == null ? null : item.TemplateName.ToLower())
                .ThenBy(item => item.TemplateName).ThenBy(item => item.PipelineId),
            ("templatename", true) => items.OrderByDescending(item => item.TemplateName != null)
                .ThenByDescending(item => item.TemplateName == null ? null : item.TemplateName.ToLower())
                .ThenByDescending(item => item.TemplateName).ThenBy(item => item.PipelineId),
            ("pinnedversion", false) => items.OrderBy(item => item.PinnedVersion).ThenBy(item => item.PipelineId),
            ("pinnedversion", true) => items.OrderByDescending(item => item.PinnedVersion).ThenBy(item => item.PipelineId),
            ("latestversion", false) => items.OrderBy(item => item.LatestVersion).ThenBy(item => item.PipelineId),
            ("latestversion", true) => items.OrderByDescending(item => item.LatestVersion).ThenBy(item => item.PipelineId),
            ("freshness", false) => items.OrderBy(item => item.Freshness).ThenBy(item => item.PipelineId),
            ("freshness", true) => items.OrderByDescending(item => item.Freshness).ThenBy(item => item.PipelineId),
            _ => items.OrderBy(item => item.TemplateName == null)
                .ThenBy(item => item.TemplateName == null ? null : item.TemplateName.ToLower())
                .ThenBy(item => item.TemplateName)
                .ThenBy(item => item.PipelineName.ToLower())
                .ThenBy(item => item.PipelineName)
                .ThenBy(item => item.PipelineId)
        };
    }

    private sealed class PipelineFleetOwnershipRow
    {
        public int PipelineId { get; init; }
        public required string PipelineName { get; init; }
        public int? ProjectId { get; init; }
        public string? TemplateReferenceName { get; init; }
        public int? TemplateReferenceVersion { get; init; }
        public required string OwnerName { get; init; }
        public required string OwnerType { get; init; }
        public int OrganizationId { get; init; }
    }
}
