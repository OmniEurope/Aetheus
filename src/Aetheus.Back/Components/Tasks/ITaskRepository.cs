// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

public interface ITaskRepository
{
    Task<(List<ServerTask> Items, int TotalCount)> GetTasksPagedAsync(
        string? search, int page, int pageSize, TaskExecutionStatus? status = null, CancellationToken ct = default);

    Task<(List<ServerTask> Items, int TotalCount)> GetTasksPagedAsync(
        string? search, int page, int pageSize, TaskExecutionStatus? status = null, List<int>? accessibleServerIds = null, int? serverId = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = true, IReadOnlyList<GridFilter>? columnFilters = null);

    /// <summary>The server names across the tasks of a scope, for the Server column
    /// filter.</summary>
    Task<TaskFilterValuesDto> GetTaskFilterValuesAsync(List<int>? accessibleServerIds, int? serverId, CancellationToken ct = default);

    Task<(List<ServerTask> Items, int TotalCount)> GetTasksByStatusesPagedAsync(
        string? search, int page, int pageSize, List<TaskExecutionStatus> statuses, List<int>? accessibleServerIds = null, CancellationToken ct = default);

    Task<ServerTask?> GetTaskWithServerAsync(int id, CancellationToken ct = default);

    Task<ServerTask?> FindTaskAsync(int id, CancellationToken ct = default);
    Task<string?> GetServerAgentVersionAsync(int serverId, CancellationToken ct = default);
    Task<Dictionary<int, int>> GetServerIdsForTasksAsync(IReadOnlyCollection<int> taskIds, CancellationToken ct = default);

    /// <summary>Current agent status of a server, or null if the server does not exist.</summary>
    Task<ServerStatus?> GetServerStatusAsync(int serverId, CancellationToken ct = default);
    /// <summary>Whether an online server has renewed its dedicated task-polling lease.</summary>
    Task<bool> IsServerTaskPollingActiveAsync(int serverId, CancellationToken ct = default);
    /// <summary>The facts that explain why a queued task of this server is not claimed, or null when the server
    /// row does not exist. Read once per task entering its hold, for the hold diagnosis.</summary>
    Task<PendingHoldServerFacts?> GetPendingHoldFactsAsync(int serverId, CancellationToken ct = default);

    Task<List<ServerTask>> GetPendingTasksAsync(int serverId, CancellationToken ct = default);
    Task<List<ServerTask>> ClaimPendingTasksAsync(
        int serverId,
        int? take = null,
        string? agentSessionId = null,
        CancellationToken ct = default);
    /// <summary>Fails a task that is still Pending (atomically: a task an agent claimed meanwhile is left
    /// alone) with a failure code and reason, and scrubs its environment. <c>false</c> when the task is
    /// no longer Pending.</summary>
    Task<bool> TryFailPendingTaskAsync(
        int taskId,
        string failureCode,
        string failureReason,
        DateTime failedAt,
        CancellationToken ct = default);
    Task<bool> HasCurrentAgentLeaseAsync(
        int taskId,
        int serverId,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default);
    Task<bool> HaveCurrentAgentLeasesAsync(
        IReadOnlyCollection<int> taskIds,
        int serverId,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default);
    Task<bool> TryStartTaskWithLeaseAsync(
        ServerTask task,
        string agentSessionId,
        long fencingToken,
        DateTime startedAt,
        CancellationToken ct = default);
    Task<bool> TryReleaseAssignedTaskWithLeaseAsync(
        ServerTask task,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default);
    Task<bool> TryStartTaskAsync(
        ServerTask task,
        DateTime startedAt,
        CancellationToken ct = default);
    Task<bool> TryCompleteTaskWithLeaseAsync(
        ServerTask task,
        string agentSessionId,
        long fencingToken,
        TaskExecutionStatus status,
        int exitCode,
        DateTime completedAt,
        string environmentVariables,
        CancellationToken ct = default);

    Task<ServerTask> AddTaskAsync(ServerTask task, CancellationToken ct = default);

    Task<PipelineStepRun?> FindPipelineStepRunAsync(int id, CancellationToken ct = default);
    Task<Dictionary<int, PipelineStepRun>> FindPipelineStepRunsByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);

    Task<List<ServerTask>> GetStaleRunningTasksAsync(TimeSpan threshold, CancellationToken ct = default);
    Task<List<ServerTask>> GetTasksFromSupersededAgentSessionsAsync(CancellationToken ct = default);
    Task<List<ServerTask>> GetStaleAssignedTasksAsync(TimeSpan threshold, CancellationToken ct = default);
    Task<List<ServerTask>> GetStalePendingTasksAsync(TimeSpan threshold, CancellationToken ct = default);
    Task<int> DeleteCompletedTasksOlderThanAsync(
        DateTime taskCutoff,
        DateTime pipelineRunCutoff,
        CancellationToken ct = default);
    Task<Dictionary<int, string>> GetTaskStatusesAsync(List<int> taskIds, int serverId, CancellationToken ct = default);
}
