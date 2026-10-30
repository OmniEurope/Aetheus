// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

public class ProjectRepository(
    AppDbContext db,
    IProjectAnalysisGradeReader analysisGrades) : IProjectRepository
{
    public async Task<(List<Project> Items, int TotalCount)> GetProjectsPagedAsync(
        string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, ProjectStatus? status = null, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = db.Projects.AsNoTracking().AsQueryable();

        if (accessibleIds is not null)
            query = query.Where(p => accessibleIds.Contains(p.Id));

        if (status.HasValue)
            query = query.Where(p => p.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search) || p.Description.Contains(search));

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        query = sortBy?.ToLowerInvariant() switch
        {
            "name" => sortDescending ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name),
            "status" => sortDescending ? query.OrderByDescending(p => p.Status) : query.OrderBy(p => p.Status),
            "createdat" => sortDescending ? query.OrderByDescending(p => p.CreatedAt) : query.OrderBy(p => p.CreatedAt),
            _ => query.OrderBy(p => p.Name)
        };

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        // The pipeline count and the last run, without the pipelines' YAML.
        await ProjectPipelineSummaries.AttachAsync(db, items, withLastRun: true, ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    public async Task<Dictionary<int, DateTime?>> GetLastGitUpdatesAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct = default)
    {
        if (projectIds.Count == 0) return [];

        // "Last git update" per project = the most recent of the internal repo's last push and any
        // external connection's last sync. Two id-scoped batched reads - no per-project N+1.
        var internalPushes = await db.GitInternalRepos.AsNoTracking()
            .Where(r => projectIds.Contains(r.ProjectId) && r.LastPushAt != null)
            .Select(r => new { r.ProjectId, Date = r.LastPushAt })
            .ToListAsync(ct).ConfigureAwait(false);

        var externalSyncs = await db.GitConnections.AsNoTracking()
            .Where(c => projectIds.Contains(c.ProjectId) && c.LastSyncedAt != null)
            .Select(c => new { c.ProjectId, Date = c.LastSyncedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        return internalPushes.Concat(externalSyncs)
            .GroupBy(x => x.ProjectId)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Date));
    }

    public async Task<Dictionary<int, int>> GetInternalRepositoryIdsAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct = default)
    {
        if (projectIds.Count == 0) return [];

        return await db.GitInternalRepos.AsNoTracking()
            .Where(repository => projectIds.Contains(repository.ProjectId))
            .GroupBy(repository => repository.ProjectId)
            .Select(group => new { ProjectId = group.Key, RepositoryId = group.Min(repository => repository.Id) })
            .ToDictionaryAsync(item => item.ProjectId, item => item.RepositoryId, ct)
            .ConfigureAwait(false);
    }

    public async Task<Dictionary<int, ProjectListInsight>> GetProjectListInsightsAsync(
        IReadOnlyCollection<int> projectIds,
        DateTime activeSessionCutoffUtc,
        CancellationToken ct = default)
    {
        if (projectIds.Count == 0) return [];

        var latestCommits = await LatestCommitsAsync(projectIds, ct).ConfigureAwait(false);
        var latestRuns = await LatestRootRunsAsync(projectIds, ct).ConfigureAwait(false);

        // Use the same cross-run, latest-domain grade as the project Quality page. Looking only at
        // the newest gate run is incorrect when quality and security are evaluated by separate
        // pipelines: the most recent run may be A while another current required domain is F.
        var gradesByProject = await analysisGrades.GetGradesAsync(projectIds, ct)
            .ConfigureAwait(false);

        var monitoredApps = await EnabledMonitoredAppsAsync(projectIds, activeSessionCutoffUtc, ct).ConfigureAwait(false);
        var productionAppsByProject = ProjectListInsightAssembler.ProductionAppsByProject(monitoredApps);

        var commitsByProject = latestCommits.ToDictionary(commit => commit.ProjectId);
        var runsByProject = latestRuns.ToDictionary(run => run.ProjectId);
        var result = new Dictionary<int, ProjectListInsight>(projectIds.Count);

        foreach (var projectId in projectIds)
        {
            result[projectId] = ProjectListInsightAssembler.Build(
                projectId,
                commitsByProject.GetValueOrDefault(projectId),
                runsByProject.GetValueOrDefault(projectId),
                gradesByProject.GetValueOrDefault(projectId)?.OverallGrade,
                productionAppsByProject.GetValueOrDefault(projectId) ?? []);
        }

        return result;
    }

    // Select root runs in the query, prioritizing the most recently started Running run over
    // finished runs. Fetch its current step in the same row, preferring running, assigned, then
    // pending non-system steps, to keep the command count bounded.
    private async Task<List<ProjectListRun>> LatestRootRunsAsync(IReadOnlyCollection<int> projectIds, CancellationToken ct)
    {
        var rows = await db.PipelineRuns
            .AsNoTracking()
            .Where(run => run.Pipeline.ProjectId.HasValue
                && projectIds.Contains(run.Pipeline.ProjectId.Value)
                && !db.PipelineStepRuns.Any(step => step.TriggeredRunId == run.Id))
            .GroupBy(run => run.Pipeline.ProjectId!.Value)
            .Select(group => group
                .OrderByDescending(run => run.Status == PipelineStatus.Running)
                .ThenByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.Id)
                .Select(run => new
                {
                    ProjectId = run.Pipeline.ProjectId!.Value,
                    run.Id,
                    PipelineName = run.Pipeline.Name,
                    run.Status,
                    run.StartedAt,
                    CurrentStep = run.StepRuns
                        .Where(step => step.Status == TaskExecutionStatus.Running
                            || step.Status == TaskExecutionStatus.Assigned
                            || (step.Status == TaskExecutionStatus.Pending && !step.IsSystem))
                        .OrderBy(step => step.Status == TaskExecutionStatus.Running ? 0
                            : step.Status == TaskExecutionStatus.Assigned ? 1 : 2)
                        .ThenBy(step => step.Order)
                        .Select(step => new { step.StageName, step.StepName })
                        .FirstOrDefault()
                })
                .First())
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.ConvertAll(row => new ProjectListRun(row.ProjectId, row.Id, row.PipelineName, row.Status, row.StartedAt,
            row.CurrentStep is { } step ? new ProjectListCurrentStep(step.StageName, step.StepName) : null));
    }

    private async Task<List<ProjectListApp>> EnabledMonitoredAppsAsync(
        IReadOnlyCollection<int> projectIds, DateTime activeSessionCutoffUtc, CancellationToken ct)
    {
        var rows = await db.MonitoredApps
            .AsNoTracking()
            .Where(app => app.Enabled && projectIds.Contains(app.ProjectId))
            .Select(app => new
            {
                app.Id,
                app.ProjectId,
                app.CurrentStatus,
                app.AnalyticsEnabled,
                EnvironmentType = app.Environment == null
                    ? (EnvironmentType?)null
                    : app.Environment.Type,
                ActiveSessionCount = db.AppAnalyticsSessions
                    .Where(session => session.MonitoredAppId == app.Id
                        && session.LastSeenAtUtc >= activeSessionCutoffUtc)
                    .Select(session => session.SessionPseudonym)
                    .Distinct()
                    .Count()
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.ConvertAll(row => new ProjectListApp(
            row.ProjectId, row.CurrentStatus, row.AnalyticsEnabled, row.EnvironmentType, row.ActiveSessionCount));
    }


    /// <summary> The newest commit of each project, one indexed top-1 lookup per project instead of
    /// numbering every commit of every listed project. A commit's date is <c>CommittedAt ??
    /// CreatedAt</c>; a sort on that expression cannot use an index, so each side is looked up on
    /// its own index (<c>ProjectId, CommittedAt, Id</c> and <c>ProjectId, CreatedAt, Id</c>) and
    /// the newer one wins. </summary>
    private async Task<List<ProjectLatestCommit>> LatestCommitsAsync(IReadOnlyCollection<int> projectIds, CancellationToken ct)
    {
        // Single-column correlated subqueries stay scalar subqueries with LIMIT 1 in SQL (an index
        // top-1 each); a row projection here would be rewritten into a ROW_NUMBER over every commit.
        var commits = db.GitCommits;
        var heads = await db.Projects.AsNoTracking()
            .Where(project => projectIds.Contains(project.Id))
            .Select(project => new
            {
                ProjectId = project.Id,
                DatedId = commits.Where(c => c.ProjectId == project.Id && c.CommittedAt != null)
                    .OrderByDescending(c => c.CommittedAt).ThenByDescending(c => c.Id).Select(c => (int?)c.Id).FirstOrDefault(),
                DatedAt = commits.Where(c => c.ProjectId == project.Id && c.CommittedAt != null)
                    .OrderByDescending(c => c.CommittedAt).ThenByDescending(c => c.Id).Select(c => c.CommittedAt).FirstOrDefault(),
                DatedSha = commits.Where(c => c.ProjectId == project.Id && c.CommittedAt != null)
                    .OrderByDescending(c => c.CommittedAt).ThenByDescending(c => c.Id).Select(c => c.Sha).FirstOrDefault(),
                DatedMessage = commits.Where(c => c.ProjectId == project.Id && c.CommittedAt != null)
                    .OrderByDescending(c => c.CommittedAt).ThenByDescending(c => c.Id).Select(c => c.Message).FirstOrDefault(),
                RecordedId = commits.Where(c => c.ProjectId == project.Id && c.CommittedAt == null)
                    .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => (int?)c.Id).FirstOrDefault(),
                RecordedAt = commits.Where(c => c.ProjectId == project.Id && c.CommittedAt == null)
                    .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => (DateTime?)c.CreatedAt).FirstOrDefault(),
                RecordedSha = commits.Where(c => c.ProjectId == project.Id && c.CommittedAt == null)
                    .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => c.Sha).FirstOrDefault(),
                RecordedMessage = commits.Where(c => c.ProjectId == project.Id && c.CommittedAt == null)
                    .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => c.Message).FirstOrDefault()
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The newer of the two sides, as COALESCE(CommittedAt, CreatedAt) DESC, Id DESC ordered them.
        return heads
            .Select(head => new[]
                {
                    head.DatedId is { } datedId && head.DatedAt is { } datedAt
                        ? new ProjectLatestCommit(head.ProjectId, datedId, head.DatedSha ?? string.Empty, head.DatedMessage, datedAt)
                        : null,
                    head.RecordedId is { } recordedId && head.RecordedAt is { } recordedAt
                        ? new ProjectLatestCommit(head.ProjectId, recordedId, head.RecordedSha ?? string.Empty, head.RecordedMessage, recordedAt)
                        : null
                }
                .OfType<ProjectLatestCommit>()
                .OrderByDescending(commit => commit.Date)
                .ThenByDescending(commit => commit.Id)
                .FirstOrDefault())
            .OfType<ProjectLatestCommit>()
            .ToList();
    }

    /// <summary>
    /// The step each run is on, as "stage · step": the running one, else the assigned one, else the
    /// next pending non-system step. One label per run; a run with none of those has no entry.
    /// </summary>
    private static async Task<Dictionary<int, string>> CurrentStepLabelsAsync(
        IQueryable<PipelineStepRun> steps, CancellationToken ct)
    {
        var candidates = await steps
            .AsNoTracking()
            .Where(step => step.Status == TaskExecutionStatus.Running
                || step.Status == TaskExecutionStatus.Assigned
                || (step.Status == TaskExecutionStatus.Pending && !step.IsSystem))
            .Select(step => new
            {
                step.PipelineRunId,
                step.StageName,
                step.StepName,
                step.Status,
                step.Order
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return candidates
            .GroupBy(step => step.PipelineRunId)
            .Select(group => group
                .OrderBy(step => step.Status == TaskExecutionStatus.Running ? 0
                    : step.Status == TaskExecutionStatus.Assigned ? 1 : 2)
                .ThenBy(step => step.Order)
                .First())
            .ToDictionary(
                step => step.PipelineRunId,
                step => $"{ProjectListInsightAssembler.NormalizeStageName(step.StageName)} · {step.StepName}");
    }

    public async Task<Project?> GetProjectDetailAsync(int id, CancellationToken ct = default)
    {
        return await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Include(p => p.Pipelines)
                .ThenInclude(pl => pl.Runs.OrderByDescending(r => r.StartedAt).Take(10))
                    .ThenInclude(r => r.StepRuns.Where(s => !s.IsSystem && s.ServerId != null).OrderBy(s => s.Order).Take(1))
                        .ThenInclude(s => s.Server)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<Dictionary<int, string>> GetActiveRunStepLabelsAsync(int projectId, CancellationToken ct = default) =>
        CurrentStepLabelsAsync(
            db.PipelineStepRuns.Where(step => step.PipelineRun.Pipeline.ProjectId == projectId
                && (step.PipelineRun.Status == PipelineStatus.Running
                    || step.PipelineRun.Status == PipelineStatus.Pending
                    || step.PipelineRun.Status == PipelineStatus.WaitingForApproval)),
            ct);

    /// <summary>Returns the latest candidate grade, matching the list insight, and its root run
    /// without loading other insights.</summary>
    public async Task<AnalysisGradeSummaryDto?> GetLatestGateGradeAsync(int projectId, CancellationToken ct = default) =>
        (await analysisGrades.GetGradesAsync([projectId], ct).ConfigureAwait(false))
            .GetValueOrDefault(projectId);

    public async Task<ProjectSectionCounts> GetProjectSectionCountsAsync(int projectId, CancellationToken ct = default)
    {
        // Read six counts in one statement without loading collections. Count distinct
        // environment-linked servers separately from direct project attachments so each section
        // count matches its rows.
        var counts = await db.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => new ProjectSectionCounts(
                db.ProjectServers.Count(ps => ps.ProjectId == projectId),
                db.Releases.Count(r => r.ProjectId == projectId),
                db.Vaults.Count(v => v.ProjectId == projectId),
                db.VariableLibraries.Count(v => v.ProjectId == projectId),
                db.Environments.Count(e => e.ProjectId == projectId),
                db.EnvironmentServers
                    .Where(es => es.Environment.ProjectId == projectId)
                    .Select(es => es.ServerId)
                    .Distinct()
                    .Count()))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return counts ?? new ProjectSectionCounts(0, 0, 0, 0, 0, 0);
    }

    public async Task<Project?> FindProjectWithPipelinesAsync(int id, CancellationToken ct = default)
    {
        return await db.Projects
            .Where(p => p.Id == id)
            .Include(p => p.Pipelines)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Project?> FindProjectAsync(int id, CancellationToken ct = default)
    {
        return await db.Projects.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task AddProjectAsync(Project project, CancellationToken ct = default)
    {
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Project>> GetAllProjectsWithRepoUrlAsync(CancellationToken ct = default)
    {
        return await db.Projects
            .AsNoTracking()
            .Where(p => p.RepositoryUrl != null && p.RepositoryUrl != "")
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<Server>> GetServersForProjectAsync(int projectId, CancellationToken ct = default)
    {
        var serverIds = await db.PipelineStepRuns
            .Where(psr => psr.PipelineRun.Pipeline.ProjectId == projectId && psr.ServerId != null)
            .Select(psr => psr.ServerId!.Value)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        return await db.Servers
            .Where(s => serverIds.Contains(s.Id))
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // The servers of the project's environments count as the project's own (R-83): the lists below are
    // ProjectServerRows, and an environment server's row has Id 0 (no ProjectServer to edit or detach).
    public async Task<List<ProjectServerRow>> GetProjectServersAsync(int projectId, CancellationToken ct = default)
    {
        return await ProjectServerRows.ForProject(db, projectId)
            .OrderBy(row => row.DisplayName)
            .ThenBy(row => row.Id)
            .ThenBy(row => row.ServerId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<ProjectServerRow> Items, int TotalCount)> GetProjectServersPageAsync(
        int projectId, string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = ProjectServerRows.ForProject(db, projectId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(row =>
                row.DisplayName.ToLower().Contains(normalized)
                || row.Host.ToLower().Contains(normalized)
                || (row.ServerName != null && row.ServerName.ToLower().Contains(normalized)));
        }

        // The header filters of the project's servers, before the count.
        query = ProjectServerListQuery.Columns.ApplyFilters(query, columnFilters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var ordered = (sortBy, sortDescending) switch
        {
            ("Host", false) => query.OrderBy(item => item.Host),
            ("Host", true) => query.OrderByDescending(item => item.Host),
            ("Port", false) => query.OrderBy(item => item.Port),
            ("Port", true) => query.OrderByDescending(item => item.Port),
            ("Type", false) => query.OrderBy(item => item.Type),
            ("Type", true) => query.OrderByDescending(item => item.Type),
            ("DisplayName", true) => query.OrderByDescending(item => item.DisplayName),
            _ => query.OrderBy(item => item.DisplayName)
        };
        // Ties (two rows with the same name) break on the identity, so a page boundary never repeats a row.
        var items = await ordered.ThenBy(item => item.Id).ThenBy(item => item.ServerId)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, totalCount);
    }

    public async Task<ProjectServer?> GetProjectServerAsync(int projectId, int projectServerId, CancellationToken ct = default)
    {
        return await db.ProjectServers
            .Where(ps => ps.ProjectId == projectId && ps.Id == projectServerId)
            .Include(ps => ps.Server)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task AddProjectServerAsync(ProjectServer projectServer, CancellationToken ct = default)
    {
        db.ProjectServers.Add(projectServer);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveProjectServerAsync(ProjectServer projectServer, CancellationToken ct = default)
    {
        db.ProjectServers.Remove(projectServer);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<ServerTask> Items, int TotalCount)> GetTasksPagedAsync(
        int projectId, string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = ProjectTasks(projectId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(task => task.Name.ToLower().Contains(normalized)
                || (task.Server != null && task.Server.Name.ToLower().Contains(normalized)));
        }

        // The header filters of the project's tasks, before the count.
        query = TaskListQuery.Columns.ApplyFilters(query, columnFilters);

        query = query.Include(task => task.Server);
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy, sortDescending) switch
        {
            ("Name", false) => query.OrderBy(task => task.Name),
            ("Name", true) => query.OrderByDescending(task => task.Name),
            ("ServerName", false) => query.OrderBy(task => task.Server!.Name),
            ("ServerName", true) => query.OrderByDescending(task => task.Server!.Name),
            ("Status", false) => query.OrderBy(task => task.Status),
            ("Status", true) => query.OrderByDescending(task => task.Status),
            ("ExitCode", false) => query.OrderBy(task => task.ExitCode),
            ("ExitCode", true) => query.OrderByDescending(task => task.ExitCode),
            ("CreatedAt", false) => query.OrderBy(task => task.CreatedAt),
            _ => query.OrderByDescending(task => task.CreatedAt)
        };
        var items = await query.Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    public Task<TaskFilterValuesDto> GetTaskFilterValuesAsync(int projectId, CancellationToken ct = default)
        => TaskListQuery.FilterValuesAsync(ProjectTasks(projectId), ct);

    private IQueryable<ServerTask> ProjectTasks(int projectId) => db.Tasks
        .Where(t => t.PipelineRun != null && t.PipelineRun.Pipeline.ProjectId == projectId)
        .AsNoTracking();

    public async Task<(List<TaskLog> Items, int TotalCount)> GetLogsPagedAsync(
        int projectId, string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.TaskLogs
            .Where(l => l.Task.PipelineRun != null && l.Task.PipelineRun.Pipeline.ProjectId == projectId)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(log => log.Message.ToLower().Contains(normalized));
        }

        // The header filters of the project's logs, before the count.
        query = ProjectLogListQuery.Columns.ApplyFilters(query, columnFilters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy, sortDescending) switch
        {
            ("Level", false) => query.OrderBy(log => log.Level),
            ("Level", true) => query.OrderByDescending(log => log.Level),
            ("Message", false) => query.OrderBy(log => log.Message),
            ("Message", true) => query.OrderByDescending(log => log.Message),
            ("Timestamp", false) => query.OrderBy(log => log.Timestamp),
            _ => query.OrderByDescending(log => log.Timestamp)
        };
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    public async Task<List<ServerTask>> GetRecentTasksAsync(int projectId, int count, CancellationToken ct = default)
    {
        return await db.Tasks
            .Where(t => t.PipelineRun != null && t.PipelineRun.Pipeline.ProjectId == projectId)
            .AsNoTracking()
            .Include(t => t.Server)
            .OrderByDescending(t => t.CreatedAt)
            .Take(count)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<PipelineRun>> GetRecentPipelineRunsAsync(int projectId, int count, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .Where(r => r.Pipeline.ProjectId == projectId)
            .Include(r => r.Pipeline)
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(count)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
