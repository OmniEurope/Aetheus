// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Monitoring;

public class MonitoringRepository(AppDbContext db) : IMonitoringRepository
{
    /// <summary>How far down a tree of triggered runs the dashboard follows a listed run's children.</summary>
    private const int MaxTriggerDepth = 4;

    public Task<int> CountRunningPipelinesAsync(List<int>? accessibleProjectIds = null, CancellationToken ct = default) =>
        CountRunningPipelinesAsync(accessibleProjectIds, [], ct);

    public Task<List<PipelineRunDto>> GetRecentRunsAsync(int groupCount, List<int>? accessibleProjectIds = null, CancellationToken ct = default) =>
        GetRecentRunsAsync(groupCount, accessibleProjectIds, [], ct);

    public async Task<List<ServerDto>> GetDashboardServersAsync(List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = db.Servers.AsNoTracking().AsQueryable();
        if (accessibleIds is not null)
            query = query.Where(s => accessibleIds.Contains(s.Id));
        // Only the columns the tile shows, ordered by the database (online first, then the most
        // recently active). The server row also carries inventory, capability and sudoers JSON the
        // dashboard never reads.
        return await query
            .OrderByDescending(s => s.Status == ServerStatus.Online)
            .ThenByDescending(s => s.LastHeartbeat)
            .ThenBy(s => s.Id)
            .Select(s => new ServerDto
            {
                Id = s.Id,
                Name = s.Name,
                Hostname = s.Hostname,
                OsDescription = s.OsDescription,
                AgentVersion = s.AgentVersion,
                Status = s.Status,
                Type = s.Type,
                LastHeartbeat = s.LastHeartbeat,
                CreatedAt = s.CreatedAt,
                OrganizationId = s.OrganizationId,
                // The tile's capability icons (build, deploy, manage) and their diagnostics tooltip, with
                // the same tri-state as every server list (ServerDataMapper): a server that never
                // phoned home has unknown capabilities (null), shown as such.
                PipelineRunnerEnabled = s.LastHeartbeat != default ? s.PipelineRunnerEnabled : null,
                DeploymentTargetAvailable = s.LastHeartbeat != default ? s.DeploymentTargetAvailable : null,
                PackageManagementAvailable = s.LastHeartbeat != default ? s.PackageManagementAvailable : null,
                CapabilityDiagnostics = ServerDataMapper.DeserializeDiagnostics(s.CapabilityDiagnosticsJson),
                DockerAvailable = s.DockerAvailable,
                RequireContainerIsolation = s.RequireContainerIsolation,
                InsecureTls = s.InsecureTls
            })
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountPendingTasksAsync(List<int>? accessibleServerIds = null, CancellationToken ct = default)
    {
        var query = db.Tasks.AsNoTracking().Where(t => t.Status == TaskExecutionStatus.Pending);
        if (accessibleServerIds is not null)
            query = query.Where(t => accessibleServerIds.Contains(t.ServerId));
        return await query.CountAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountRunningPipelinesAsync(List<int>? accessibleProjectIds, List<int>? accessiblePipelineIds, CancellationToken ct)
    {
        var query = FilterRunsByAccess(db.PipelineRuns.AsNoTracking(), accessibleProjectIds, accessiblePipelineIds)
            .Where(r => r.Status == PipelineStatus.Running);
        return await query.CountAsync(ct).ConfigureAwait(false);
    }

    /// <summary> The runs the tile lists and nothing more. The tile shows <paramref
    /// name="groupCount"/> groups, each a run nobody triggered with the runs it triggered nested
    /// under it, so the newest such roots are read, then their descendants, instead of the hundred
    /// newest runs of every kind. </summary>
    public async Task<List<PipelineRunDto>> GetRecentRunsAsync(int groupCount, List<int>? accessibleProjectIds, List<int>? accessiblePipelineIds, CancellationToken ct)
    {
        var accessible = FilterRunsByAccess(db.PipelineRuns.AsNoTracking(), accessibleProjectIds, accessiblePipelineIds);
        var runIds = await GetRunGroupIdsAsync(accessible, groupCount, ct).ConfigureAwait(false);
        if (runIds.Count == 0) return [];

        var runs = await db.PipelineRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Select(PipelineRunListRows.Projection)
            .ToListAsync(ct).ConfigureAwait(false);
        var hydrated = await PipelineAdvisorySteps.ApplyToListAsync(
            db, runs.Select(PipelineRunListRows.HydrateWarnings).ToList(), ct).ConfigureAwait(false);
        var graded = await PipelineRunGradeAggregation.ApplyAsync(db, hydrated, ct).ConfigureAwait(false);
        return await PipelineRunRepositoryLinks.ApplyAsync(db, graded, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The ids of the newest <paramref name="groupCount"/> root runs (no readable run triggered them)
    /// and of the runs they triggered, down to <see cref="MaxTriggerDepth"/> levels. Ids only: the rows
    /// are projected once, afterwards.
    /// </summary>
    private async Task<List<int>> GetRunGroupIdsAsync(IQueryable<PipelineRun> accessible, int groupCount, CancellationToken ct)
    {
        var accessibleIds = accessible.Select(run => run.Id);
        var rootIds = await accessible
            .Where(run => !db.PipelineStepRuns.Any(step => step.TriggeredRunId == run.Id
                && accessibleIds.Contains(step.PipelineRunId)))
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .Take(Math.Max(0, groupCount))
            .Select(run => run.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var shown = new HashSet<int>(rootIds);
        var parents = rootIds;
        for (var depth = 0; depth < MaxTriggerDepth && parents.Count > 0; depth++)
        {
            var parentIds = parents;
            var childIds = await db.PipelineStepRuns.AsNoTracking()
                .Where(step => parentIds.Contains(step.PipelineRunId) && step.TriggeredRunId != null)
                .Select(step => step.TriggeredRunId!.Value)
                .Where(childId => accessibleIds.Contains(childId))
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            parents = childIds.Where(shown.Add).ToList();
        }
        return [.. shown];
    }

    public async Task<int> CountProjectsAsync(List<int>? accessibleProjectIds = null, CancellationToken ct = default)
    {
        var query = db.Projects.AsNoTracking().AsQueryable();
        if (accessibleProjectIds is not null)
            query = query.Where(p => accessibleProjectIds.Contains(p.Id));

        return await query.CountAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Project>> GetRecentProjectsAsync(int count, List<int>? accessibleProjectIds = null, CancellationToken ct = default)
    {
        var query = db.Projects.AsNoTracking().AsQueryable();
        if (accessibleProjectIds is not null)
            query = query.Where(p => accessibleProjectIds.Contains(p.Id));

        var projects = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Take(count)
            .ToListAsync(ct).ConfigureAwait(false);
        // The dashboard tile only counts the pipelines; their YAML is not loaded for that.
        await ProjectPipelineSummaries.AttachAsync(db, projects, withLastRun: false, ct).ConfigureAwait(false);
        return projects;
    }

    private static IQueryable<PipelineRun> FilterRunsByAccess(
        IQueryable<PipelineRun> query,
        List<int>? accessibleProjectIds,
        List<int>? accessiblePipelineIds)
    {
        if (accessibleProjectIds is null || accessiblePipelineIds is null)
            return query;

        return query.Where(r =>
            accessiblePipelineIds.Contains(r.PipelineId)
            || (r.Pipeline.ProjectId.HasValue && accessibleProjectIds.Contains(r.Pipeline.ProjectId.Value))
            || (r.Pipeline.Environment != null
                && r.Pipeline.Environment.ProjectId.HasValue
                && accessibleProjectIds.Contains(r.Pipeline.Environment.ProjectId.Value))
            || (r.Pipeline.ProjectServer != null && accessibleProjectIds.Contains(r.Pipeline.ProjectServer.ProjectId)));
    }

    public async Task<List<ServerMetric>> GetServerMetricsSinceAsync(
        int serverId,
        DateTime since,
        CancellationToken ct = default,
        DateTime? afterUtc = null,
        int take = 1_000)
    {
        var query = db.ServerMetrics
            .AsNoTracking()
            .Where(m => m.ServerId == serverId
                && m.Timestamp >= since
                && (!afterUtc.HasValue || m.Timestamp > afterUtc.Value));

        // Select the newest bounded window in SQL, then restore chronological order for charts.
        var rows = await query
            .OrderByDescending(m => m.Timestamp)
            .Take(Math.Clamp(take, 1, 2_000))
            .ToListAsync(ct).ConfigureAwait(false);
        rows.Reverse();
        return rows;
    }
}
