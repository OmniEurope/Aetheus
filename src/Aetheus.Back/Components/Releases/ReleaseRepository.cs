// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Releases;

public class ReleaseRepository(AppDbContext db) : IReleaseRepository
{
    public async Task<(List<Release> Items, int TotalCount)> GetReleasesPagedAsync(
        string? search, int? projectId, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = true, bool deployableOnly = false,
        IReadOnlyList<GridFilter>? columnFilters = null, IReadOnlyCollection<int>? releaseIds = null)
    {
        var query = ScopedReleases(projectId, accessibleIds);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(r => r.Version.Contains(search) || r.BranchName.Contains(search));

        // A release restore requires retained payload regardless of status, including superseded
        // releases. Deployment separately validates ancestry and grade; the picker exposes status.
        if (deployableOnly)
            query = query.Where(r => r.Artifacts.Any());

        query = ApplyColumnFilters(query, columnFilters, releaseIds);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        var items = await query
            .Include(r => r.Project)
            .Include(r => r.Artifacts)
            .AsSplitQuery()
            // Keep unpublished (PublishedAt == null) releases LAST, matching the old `?? MinValue`
            // behavior WITHOUT wrapping every row's PublishedAt in a COALESCE: Postgres DESC orders
            // NULLs FIRST, so a plain `OrderByDescending(PublishedAt)` would surface drafts on top.
            // The leading `PublishedAt != null` key (`ORDER BY (PublishedAt IS NOT NULL) DESC`) pins
            // published releases above drafts. (Postgres still sorts on that boolean expression - the
            // btree index on PublishedAt only helps the tie-break within each null/non-null group, not
            // the leading key; the win here is dropping the per-row COALESCE, not a full index sort.)
            .OrderByProperty(sortBy, sortDescending, ordered => ordered
                .OrderByDescending(r => r.PublishedAt != null)
                .ThenByDescending(r => r.PublishedAt)
                .ThenByDescending(r => r.Id))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, totalCount);
    }

    private IQueryable<Release> ScopedReleases(int? projectId, List<int>? accessibleIds)
    {
        var query = db.Releases.AsNoTracking().AsQueryable();

        if (accessibleIds is not null)
            query = query.Where(r => accessibleIds.Contains(r.Id));

        if (projectId.HasValue)
            query = query.Where(r => r.ProjectId == projectId.Value);

        return query;
    }

    /// <summary> The header filters, after the scope and before the count. The source pipeline
    /// filter arrives already resolved to <paramref name="releaseIds"/>. </summary>
    private static IQueryable<Release> ApplyColumnFilters(
        IQueryable<Release> query, IReadOnlyList<GridFilter>? columnFilters, IReadOnlyCollection<int>? releaseIds)
    {
        if (releaseIds is not null)
            query = query.Where(r => releaseIds.Contains(r.Id));
        return ReleaseListQuery.Columns.ApplyFilters(query, columnFilters);
    }

    public async Task<List<ReleaseFilterFact>> GetReleaseFilterFactsAsync(
        int? projectId, List<int>? accessibleIds, int? serverId, CancellationToken ct = default)
    {
        var query = serverId is { } server ? ReleasesForServerQuery(server) : ScopedReleases(projectId, accessibleIds);
        return await query
            .Select(r => new ReleaseFilterFact(r.Id, r.Project.Name, r.PipelineRunId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<Release>> GetProjectReleasesAsync(int projectId, CancellationToken ct = default)
    {
        return await db.Releases
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .Include(r => r.Project)
            .Include(r => r.Artifacts)
            .AsSplitQuery()
            // Keep unpublished (null PublishedAt) releases LAST - see GetReleasesPagedAsync.
            .OrderByDescending(r => r.PublishedAt != null)
            .ThenByDescending(r => r.PublishedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Release?> FindReleaseAsync(int id, CancellationToken ct = default)
    {
        return await db.Releases
            .Include(r => r.Project)
            .Include(r => r.Artifacts)
            .Include(r => r.Commits)
            .Include(r => r.Branches)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<Release?> FindByVersionAsync(int projectId, string version, CancellationToken ct = default)
    {
        return await db.Releases
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Version == version, ct)
            .ConfigureAwait(false);
    }

    public async Task<List<Release>> GetDeployedProjectReleasesAsync(int projectId, CancellationToken ct = default) =>
        await db.Releases
            .Where(release => release.ProjectId == projectId && release.Status == ReleaseStatus.Deployed)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>The template a "Revenir à N-1" pipeline extends.</summary>
    internal const string RevertTemplateName = "host-bluegreen-revert";

    public async Task<Dictionary<int, ReleaseRedeployTarget>> GetRedeployTargetsAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct = default)
    {
        if (projectIds.Count == 0) return [];
        // Superseded means "was deployed, then replaced", so the newest of them is the release
        // production ran just before the live one: the same order the retention window uses.
        var previous = (await db.Releases.AsNoTracking()
                .Where(release => projectIds.Contains(release.ProjectId) && release.Status == ReleaseStatus.Superseded)
                .Select(release => new { release.Id, release.ProjectId, At = release.PublishedAt ?? release.DetectedAt })
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(release => release.ProjectId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(release => release.At).First().Id);
        if (previous.Count == 0) return [];

        // A deploy step rewrites the live release's PipelineRunId to the run that deployed it.
        var projects = previous.Keys.ToList();
        var liveRuns = await db.Releases.AsNoTracking()
            .Where(release => projects.Contains(release.ProjectId)
                && release.Status == ReleaseStatus.Deployed && release.PipelineRunId != null)
            .Select(release => new { release.ProjectId, RunId = release.PipelineRunId!.Value })
            .ToListAsync(ct).ConfigureAwait(false);
        var runIds = liveRuns.Select(run => run.RunId).ToList();
        var deployPipelines = await db.PipelineRuns.AsNoTracking()
            .Where(run => runIds.Contains(run.Id) && run.ParametersJson != null
                && run.ParametersJson.Contains("candidateVersion"))
            .Select(run => new { run.Id, run.PipelineId })
            .ToDictionaryAsync(run => run.Id, run => run.PipelineId, ct).ConfigureAwait(false);

        // Identify the project quick-return pipeline through its extended template.
        var revertPipelines = (await db.Pipelines.AsNoTracking()
                .Where(pipeline => pipeline.ProjectId != null && projects.Contains(pipeline.ProjectId.Value)
                    && pipeline.TemplateReferenceName == RevertTemplateName)
                .Select(pipeline => new { ProjectId = pipeline.ProjectId!.Value, pipeline.Id })
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(pipeline => pipeline.ProjectId)
            .ToDictionary(group => group.Key, group => group.Min(pipeline => pipeline.Id));

        return previous.ToDictionary(
            entry => entry.Key,
            entry =>
            {
                var liveRun = liveRuns.FirstOrDefault(run => run.ProjectId == entry.Key);
                int? pipelineId = liveRun is not null && deployPipelines.TryGetValue(liveRun.RunId, out var id) ? id : null;
                return new ReleaseRedeployTarget(
                    entry.Value, pipelineId, revertPipelines.TryGetValue(entry.Key, out var revert) ? revert : null);
            });
    }

    public async Task AddReleaseAsync(Release release, CancellationToken ct = default)
    {
        db.Releases.Add(release);
        StampCreators();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stamp the creator only when a release is first published, independently of later
    /// runs that overwrite PipelineRunId. Do not assign a creator retroactively to an already
    /// published release with unknown provenance.</summary>
    private void StampCreators()
    {
        foreach (var entry in db.ChangeTracker.Entries<Release>())
        {
            var release = entry.Entity;
            if (release.CreatedByPipelineRunId is not null
                || release.PipelineRunId is not { } runId
                || release.PublishedAt is null)
                continue;
            if (entry.State == EntityState.Added
                || entry.Property(r => r.PublishedAt).OriginalValue is null)
                release.CreatedByPipelineRunId = runId;
        }
    }

    public async Task<Release?> FindByPipelineRunIdAsync(int pipelineRunId, CancellationToken ct = default)
    {
        return await db.Releases
            .FirstOrDefaultAsync(r => r.PipelineRunId == pipelineRunId, ct)
            .ConfigureAwait(false);
    }

    public async Task<Release?> FindPreviousPublishedWithArtifactAsync(Release release, CancellationToken ct = default)
    {
        // A release can only be rolled back inside one deployment cohort. Older rows that predate
        // the cohort metadata, or a release spanning several cohorts, are intentionally ineligible:
        // choosing an arbitrary environment would be worse than refusing the request.
        var cohorts = release.Artifacts
            .Select(artifact => artifact.EnvironmentName)
            .Where(environmentName => !string.IsNullOrWhiteSpace(environmentName))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (cohorts.Count != 1) return null;

        var cohort = cohorts[0];
        var publishedAt = release.PublishedAt ?? release.DetectedAt;
        return await db.Releases
            .Where(candidate => candidate.ProjectId == release.ProjectId
                && candidate.Id != release.Id
                && candidate.PublishedAt != null
                && (candidate.Status == ReleaseStatus.Published
                    || candidate.Status == ReleaseStatus.Promoted
                    || candidate.Status == ReleaseStatus.Deployed
                    || candidate.Status == ReleaseStatus.Superseded)
                && (candidate.PublishedAt < publishedAt
                    || (candidate.PublishedAt == publishedAt && candidate.Id < release.Id))
                && candidate.Artifacts.Any(artifact => artifact.EnvironmentName == cohort))
            .Include(candidate => candidate.Project)
            .Include(candidate => candidate.Artifacts.Where(artifact => artifact.EnvironmentName == cohort))
            .OrderByDescending(candidate => candidate.PublishedAt)
            .ThenByDescending(candidate => candidate.Id)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddRollbackAsync(ReleaseRollback rollback, CancellationToken ct = default)
    {
        db.ReleaseRollbacks.Add(rollback);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<ReleaseRollback?> FindRollbackByPipelineRunIdAsync(int pipelineRunId, CancellationToken ct = default)
    {
        return await db.ReleaseRollbacks
            .Include(r => r.SourceRelease).ThenInclude(r => r.Project)
            .Include(r => r.TargetRelease).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(r => r.PipelineRunId == pipelineRunId, ct)
            .ConfigureAwait(false);
    }

    public async Task<ReleaseRollback?> FindRollbackAsync(int id, CancellationToken ct = default)
    {
        return await db.ReleaseRollbacks
            .Include(r => r.SourceRelease).ThenInclude(r => r.Project)
            .Include(r => r.TargetRelease).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<List<Release>> GetByPipelineRunIdAsync(int pipelineRunId, CancellationToken ct = default)
    {
        // Resolve publishing runs through their release-step RELEASE_ID output because
        // Release.PipelineRunId records the latest reporting run, including deployments.
        var publishedIds = await PublishedReleaseIdsAsync(pipelineRunId, ct).ConfigureAwait(false);
        var runProjectId = await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == pipelineRunId)
            .Select(run => run.Pipeline.ProjectId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return await db.Releases
            .AsNoTracking()
            .Where(r => r.PipelineRunId == pipelineRunId
                || (publishedIds.Contains(r.Id) && r.ProjectId == runProjectId))
            .Include(r => r.Project)
            .Include(r => r.Artifacts)
            // Keep unpublished (null PublishedAt) releases LAST - see GetReleasesPagedAsync.
            .OrderByDescending(r => r.PublishedAt != null)
            .ThenByDescending(r => r.PublishedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>The ids of the releases a successful step of this run created, read from the
    /// <c>RELEASE_ID</c> output variable the agent's release step publishes.</summary>
    private async Task<List<int>> PublishedReleaseIdsAsync(int pipelineRunId, CancellationToken ct)
    {
        var outputs = await db.PipelineStepRuns.AsNoTracking()
            .Where(step => step.PipelineRunId == pipelineRunId
                && step.Status == TaskExecutionStatus.Success && step.OutputVariablesJson != null)
            .Select(step => step.OutputVariablesJson)
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. outputs
            .Select(json => PipelineRunHelpers.DeserializeResolvedVariables(json)
                .TryGetValue("RELEASE_ID", out var id) && int.TryParse(id, out var releaseId) ? releaseId : 0)
            .Where(releaseId => releaseId > 0)
            .Distinct()];
    }

    public async Task<int> GetMaxBuildNumberAsync(int projectId, CancellationToken ct = default)
    {
        return await db.Releases
            .Where(r => r.ProjectId == projectId)
            .Select(r => (int?)r.BuildNumber)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? 0;
    }

    /// <summary>
    /// One page of a server's releases, newest first, with the total. A360-18: the unpaged variant
    /// below loads every release of every project the server ever touched, on a table whose whole point
    /// is long retention - fine on a fresh install, an out-of-memory waiting to happen after a year.
    /// </summary>
    public async Task<(List<Release> Items, int TotalCount)> GetReleasesForServerPagedAsync(
        int serverId, int page, int pageSize, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = true,
        IReadOnlyList<GridFilter>? columnFilters = null, IReadOnlyCollection<int>? releaseIds = null)
    {
        var query = ApplyColumnFilters(ReleasesForServerQuery(serverId), columnFilters, releaseIds);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query.OrderByProperty(sortBy, sortDescending, OrderForDisplay)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    /// <summary>
    /// Releases of every project a server is involved in, either by having executed one of its
    /// pipeline steps or by being explicitly linked to it.
    ///
    /// It lived in ServerRepository, which made Servers depend on Pipelines to enrich the result with
    /// the pipeline each release came from. The query is about releases, so it belongs here: Releases
    /// already sits above Pipelines and already performs that enrichment for its own views.
    /// </summary>
    public async Task<List<Release>> GetReleasesForServerAsync(int serverId, CancellationToken ct = default) =>
        await OrderForDisplay(ReleasesForServerQuery(serverId))
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>The projects a server is linked to, or has ever executed a pipeline for.</summary>
    private IQueryable<Release> ReleasesForServerQuery(int serverId)
    {
        var executedProjectIds = db.PipelineStepRuns
            .Where(step => step.ServerId == serverId)
            .Select(step => step.PipelineRun.Pipeline.ProjectId)
            .Where(projectId => projectId != null)
            .Select(projectId => projectId!.Value);
        // Linked directly, or through an environment of the project (R-83).
        var linkedProjectIds = db.ProjectIdsOfServer(serverId);
        var projectIds = executedProjectIds.Union(linkedProjectIds).Distinct();

        return db.Releases
            .Where(release => projectIds.Contains(release.ProjectId))
            .Include(release => release.Project)
            .AsNoTracking();
    }

    /// <summary>
    /// Unpublished (null PublishedAt) releases LAST, without the non-sargable COALESCE - the same NULL
    /// ordering every other release view uses, so draft placement stays consistent.
    /// </summary>
    private static IOrderedQueryable<Release> OrderForDisplay(IQueryable<Release> query)
        => query
            .OrderByDescending(release => release.PublishedAt != null)
            .ThenByDescending(release => release.PublishedAt)
            .ThenByDescending(release => release.Id);

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        StampCreators();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
