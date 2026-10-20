// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AiTasks;

public interface IAiTaskRepository
{
    Task<(List<AiRunnerProfile> Items, int Total)> GetProfilesPageAsync(
        string? search, int page, int pageSize, CancellationToken ct,
        IReadOnlyList<GridFilter>? columnFilters = null,
        string? sortBy = null, bool sortDescending = false);
    Task<List<AiRunnerProfile>> GetProfilesForOrganizationsAsync(
        List<int> organizationIds, CancellationToken ct);
    Task<AiRunnerProfile?> FindProfileAsync(int id, CancellationToken ct);
    /// <summary>Every profile of one organization, most preferred first (lowest priority, then lowest
    /// id): what a pipeline AI step picks its profile from. Untracked.</summary>
    Task<List<AiRunnerProfile>> GetOrganizationProfilesByPriorityAsync(int organizationId, CancellationToken ct);
    Task AddProfileAsync(AiRunnerProfile profile, CancellationToken ct);
    Task RemoveProfileAsync(AiRunnerProfile profile, CancellationToken ct);
    Task<bool> ProfileHasDefinitionsAsync(int profileId, CancellationToken ct);
    /// <summary>Tracked API profiles (known provider, stored key) whose model list was not read since
    /// <paramref name="checkedBefore"/>, oldest attempt first.</summary>
    Task<List<AiRunnerProfile>> GetProfilesDueForModelListAsync(DateTime checkedBefore, CancellationToken ct);

    Task<(List<AiTaskDefinition> Items, int Total)> GetDefinitionsPageAsync(
        string? search, int page, int pageSize, int? projectId, int? serverId,
        List<int>? accessibleProjectIds, List<int>? accessibleServerIds, CancellationToken ct,
        IReadOnlyList<GridFilter>? columnFilters = null,
        string? sortBy = null, bool sortDescending = false);
    Task<AiTaskDefinition?> FindDefinitionAsync(int id, CancellationToken ct);
    Task AddDefinitionAsync(AiTaskDefinition definition, CancellationToken ct);
    Task RemoveDefinitionAsync(AiTaskDefinition definition, CancellationToken ct);
    Task<List<AiTaskDefinition>> GetScheduledDefinitionsAsync(CancellationToken ct);
    /// <summary>Enabled definitions triggered by <paramref name="eventType"/> and owned by the event's project or server.</summary>
    Task<List<AiTaskDefinition>> GetDefinitionsForEventAsync(
        string eventType, int? projectId, int? serverId, CancellationToken ct);
    Task<Server?> ResolveExecutionServerAsync(AiTaskDefinition definition, CancellationToken ct);
    Task<int?> GetProjectOrganizationIdAsync(int projectId, CancellationToken ct);
    Task<int?> GetServerOrganizationIdAsync(int serverId, CancellationToken ct);
    Task<GitInternalRepo?> GetPrimaryProjectRepositoryAsync(int projectId, CancellationToken ct);
    Task<int> GetActiveRunCountAsync(int serverId, CancellationToken ct);

    Task<ServerTask> AddTaskAsync(ServerTask task, CancellationToken ct);
    Task<ServerTask?> FindTaskAsync(int id, CancellationToken ct);
    Task<AiRunResult?> FindResultByTaskAsync(int taskId, CancellationToken ct);
    Task<AiRunResult?> FindResultAsync(int id, CancellationToken ct);
    Task AddResultAsync(AiRunResult result, CancellationToken ct);
    Task<(List<AiRunResult> Items, int Total)> GetResultsPageAsync(
        int? definitionId, int? pipelineRunId, int page, int pageSize, CancellationToken ct);
    Task<List<AiProfileConsumptionDto>> GetConsumptionByProfileAsync(
        DateTime since, int? projectId, CancellationToken ct);
    /// <summary>Own-read for the run-scoped RBAC check; see the implementation for why.</summary>
    Task<int?> GetRunPipelineIdAsync(int pipelineRunId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
