// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Read-only projection used by the pipeline parent/leaf graph.</summary>
internal sealed class PipelineDependencyGraphRepository(AppDbContext db)
{
    /// <summary>Restricts the dependency graph by <paramref name="projectId"/> and <paramref
    /// name="serverId"/> before loading unrelated fleet data.</summary>
    internal async Task<List<PipelineDto>> GetAsync(
        List<int>? accessibleIds = null, int? serverId = null, int? projectId = null,
        CancellationToken ct = default)
    {
        var query = db.Pipelines.AsNoTracking().AsQueryable();
        if (accessibleIds is not null)
            query = query.Where(p => accessibleIds.Contains(p.Id));
        if (projectId is { } scopedProjectId)
        {
            query = query.Where(pipeline =>
                (pipeline.ProjectId ?? pipeline.Environment!.ProjectId ?? pipeline.ProjectServer!.ProjectId)
                == scopedProjectId);
        }

        if (serverId.HasValue)
        {
            var usedPipelineIds = db.PipelineStepRuns
                .Where(step => step.ServerId == serverId.Value)
                .Select(step => step.PipelineRun.PipelineId)
                .Distinct();
            query = query.Where(pipeline => usedPipelineIds.Contains(pipeline.Id));
        }

        var pipelines = await Project(query.OrderBy(pipeline => pipeline.Name))
            .AsSplitQuery()
            .ToListAsync(ct).ConfigureAwait(false);
        return await PipelineRunGradeAggregation.ApplyToRecentRunsAsync(db, pipelines, ct).ConfigureAwait(false);
    }

    internal async Task<(List<PipelineDto> Items, List<PipelineDto> Identities, int TotalCount)> GetPageAsync(
        PipelinePaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var query = db.Pipelines.AsNoTracking().AsQueryable();
        if (accessibleIds is not null)
            query = query.Where(pipeline => accessibleIds.Contains(pipeline.Id));
        if (request.ProjectId.HasValue)
            query = query.Where(pipeline => pipeline.ProjectId == request.ProjectId.Value);
        if (request.EnvironmentId.HasValue)
            query = query.Where(pipeline => pipeline.EnvironmentId == request.EnvironmentId.Value);
        else if (request.ProjectServerId.HasValue)
            query = query.Where(pipeline => pipeline.ProjectServerId == request.ProjectServerId.Value);
        if (request.ServerId.HasValue)
        {
            var serverId = request.ServerId.Value;
            var pipelineIds = db.PipelineStepRuns.Where(step => step.ServerId == serverId)
                .Select(step => step.PipelineRun.PipelineId).Distinct();
            query = query.Where(pipeline => pipelineIds.Contains(pipeline.Id));
        }
        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(pipeline => pipeline.Name.Contains(request.Search)
                || (pipeline.Project != null && pipeline.Project.Name.Contains(request.Search)));
        if (request.TriggerType.HasValue)
            query = query.Where(pipeline => pipeline.TriggerType == request.TriggerType.Value);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await Project(query.OrderBy(pipeline => pipeline.Name)
                .Skip((page - 1) * pageSize).Take(pageSize))
            .AsSplitQuery()
            .ToListAsync(ct).ConfigureAwait(false);
        items = await PipelineRunGradeAggregation.ApplyToRecentRunsAsync(db, items, ct).ConfigureAwait(false);

        // Reference resolution only needs identity columns. Unlike the former endpoint this does not
        // load or parse every YAML definition; only the requested page carries YAML.
        var identityQuery = db.Pipelines.AsNoTracking().AsQueryable();
        if (accessibleIds is not null)
            identityQuery = identityQuery.Where(pipeline => accessibleIds.Contains(pipeline.Id));
        var identities = await identityQuery.Select(pipeline => new PipelineDto
        {
            Id = pipeline.Id,
            Name = pipeline.Name,
            ProjectId = pipeline.ProjectId
        }).ToListAsync(ct).ConfigureAwait(false);
        return (items, identities, totalCount);
    }

    private IQueryable<PipelineDto> Project(IQueryable<Data.Entities.Pipeline> query) => query.Select(pipeline => new PipelineDto
    {
        Id = pipeline.Id,
        Name = pipeline.Name,
        OwnerUsername = pipeline.CreatedByUsername,
        OwnerInactive = !db.Users.Any(user => user.Username == pipeline.CreatedByUsername && user.IsActive),
        ProjectId = pipeline.ProjectId,
        ProjectName = pipeline.Project != null ? pipeline.Project.Name : null,
        TriggerType = pipeline.TriggerType,
        YamlDefinition = pipeline.YamlDefinition,
        RecentRuns = pipeline.Runs
            .OrderByDescending(run => run.StartedAt)
            .Take(5)
            .Select(run => new PipelineRunSummaryDto
            {
                Id = run.Id,
                Status = run.Status,
                StartedAt = run.StartedAt,
                CompletedAt = run.CompletedAt
            })
            .ToList()
    });
}
