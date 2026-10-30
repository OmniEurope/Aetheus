// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

public sealed record ProjectListInsight(
    int ProjectId,
    int? LastCommitId,
    string? LastCommitSha,
    string? LastCommitMessage,
    DateTime? LastCommitAt,
    int? LastRunId,
    string? LastRunName,
    PipelineStatus? LastRunStatus,
    DateTime? LastRunAt,
    AnalysisGrade? LatestGateGrade,
    ProjectProductionStatus ProductionStatus,
    int? OnlineUserCount,
    bool LastRunIsActive = false,
    string? LastRunCurrentStep = null);

public interface IProjectRepository
{
    Task<(List<Project> Items, int TotalCount)> GetProjectsPagedAsync(
        string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, ProjectStatus? status = null, List<int>? accessibleIds = null, CancellationToken ct = default);

    /// <summary>Latest git activity per project (internal last-push and/or external last-sync),
    /// batched by id so the projects list stays a single extra query rather than N+1.</summary>
    Task<Dictionary<int, DateTime?>> GetLastGitUpdatesAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct = default);

    Task<Dictionary<int, int>> GetInternalRepositoryIdsAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct = default);
    Task<Dictionary<int, ProjectListInsight>> GetProjectListInsightsAsync(
        IReadOnlyCollection<int> projectIds, DateTime activeSessionCutoffUtc, CancellationToken ct = default);

    /// <summary>Returns only the current project quality grade required by the detail
    /// page.</summary>
    Task<AnalysisGradeSummaryDto?> GetLatestGateGradeAsync(int projectId, CancellationToken ct = default);

    Task<Project?> GetProjectDetailAsync(int id, CancellationToken ct = default);

    /// <summary>Current display label for each active pipeline run in a project. This is queried
    /// separately so the project detail does not load every step of every recent run.</summary>
    Task<Dictionary<int, string>> GetActiveRunStepLabelsAsync(int projectId, CancellationToken ct = default);

    /// <summary>S-TECH-N8R3: per-section entity counts for a project's enriched detail tiles.</summary>
    Task<ProjectSectionCounts> GetProjectSectionCountsAsync(int projectId, CancellationToken ct = default);

    Task<Project?> FindProjectWithPipelinesAsync(int id, CancellationToken ct = default);

    Task<Project?> FindProjectAsync(int id, CancellationToken ct = default);

    Task AddProjectAsync(Project project, CancellationToken ct = default);


    Task SaveChangesAsync(CancellationToken ct = default);

    Task<List<Project>> GetAllProjectsWithRepoUrlAsync(CancellationToken ct = default);

    Task<List<Server>> GetServersForProjectAsync(int projectId, CancellationToken ct = default);

    // ProjectServer CRUD
    /// <summary>The project's servers: its own links and the servers of its environments (R-83). Read-only
    /// rows: an environment server has no <c>ProjectServer</c> (its row Id is 0), so nothing here feeds an
    /// update or a delete; those go through <see cref="GetProjectServerAsync"/>.</summary>
    Task<List<ProjectServerRow>> GetProjectServersAsync(int projectId, CancellationToken ct = default);
    Task<(List<ProjectServerRow> Items, int TotalCount)> GetProjectServersPageAsync(
        int projectId, string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null);
    Task<ProjectServer?> GetProjectServerAsync(int projectId, int projectServerId, CancellationToken ct = default);
    Task AddProjectServerAsync(ProjectServer projectServer, CancellationToken ct = default);
    Task RemoveProjectServerAsync(ProjectServer projectServer, CancellationToken ct = default);

    Task<(List<ServerTask> Items, int TotalCount)> GetTasksPagedAsync(
        int projectId, string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null);

    /// <summary>The server names across a project's tasks, for the Server column filter.</summary>
    Task<TaskFilterValuesDto> GetTaskFilterValuesAsync(int projectId, CancellationToken ct = default);

    Task<(List<TaskLog> Items, int TotalCount)> GetLogsPagedAsync(
        int projectId, string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null);

    Task<List<ServerTask>> GetRecentTasksAsync(int projectId, int count, CancellationToken ct = default);

    Task<List<PipelineRun>> GetRecentPipelineRunsAsync(int projectId, int count, CancellationToken ct = default);
}

/// <summary>Keeps <paramref name="EnvironmentServers"/> separate from <paramref name="Servers"/>
/// because the Servers section lists only direct attachments. Combining them would count rows
/// absent from that section.</summary>
public sealed record ProjectSectionCounts(
    int Servers,
    int Releases,
    int Vaults,
    int Libraries,
    int Environments,
    int EnvironmentServers);
