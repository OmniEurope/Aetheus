// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

public interface ITaskService
{
    Task<PaginatedResult<ServerTaskDto>> GetTasksAsync(TaskPaginationRequest request, CancellationToken ct = default);

    Task<PaginatedResult<ServerTaskDto>> GetTasksAsync(TaskPaginationRequest request, List<int>? accessibleServerIds = null, CancellationToken ct = default);
    Task<ServerTaskDto?> GetTaskAsync(int id, CancellationToken ct = default);
    Task<ServerTaskDto> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default);
    Task<ServerTaskDto> CreateOperationAsync(CreateOperationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Throws a <see cref="ConflictException"/> when the target server is known and its agent is not
    /// online: a task queued for it would sit Pending with nobody to claim it. CreateTask/CreateOperation
    /// apply it themselves; <see cref="TaskQueuePersistence"/> applies it for the module services that
    /// persist through their own repository (SITE-OFFLINE-001).
    /// </summary>
    Task EnsureServerOnlineAsync(int serverId, CancellationToken ct = default);

    /// <summary>
    /// Pushes a "TaskQueued" SignalR event for a task that was persisted OUTSIDE the CreateTask/
    /// CreateOperation paths (e.g. the service-management collaborator queues service-action and
    /// package install/uninstall tasks straight through the repository). Without this the top-bar
    /// tracker never learns the task exists, so it shows neither the queued task nor (because
    /// MarkRunning ignores untracked ids) its later progress, until a manual reseed.
    /// </summary>
    Task NotifyTaskQueuedAsync(ServerTask task, string? serverNameOverride = null, CancellationToken ct = default);
    Task<List<PendingTaskDto>> GetPendingTasksAsync(
        int serverId,
        int? take = null,
        string? agentSessionId = null,
        CancellationToken ct = default);
    Task<bool> ReleaseAssignedTaskAsync(int id, AgentTaskLeaseRequest lease, CancellationToken ct = default);
    Task<bool> StartTaskAsync(int id, CancellationToken ct = default);
    Task<bool> StartTaskAsync(int id, AgentTaskLeaseRequest lease, CancellationToken ct = default);
    Task<bool> CompleteTaskAsync(int id, TaskResultDto result, CancellationToken ct = default);
    Task<bool> ReportDeploymentBuildRefusalAsync(
        int id,
        DeploymentBuildRefusalReport report,
        CancellationToken ct = default);
    Task<bool> CancelTaskAsync(int id, CancellationToken ct = default);
    Task<Dictionary<int, string>> GetTaskStatusesAsync(List<int> taskIds, int serverId, CancellationToken ct = default);
    Task<int?> GetTaskServerIdAsync(int taskId, CancellationToken ct = default);
    Task<bool> AllowsLegacyUnfencedTaskProtocolAsync(int serverId, CancellationToken ct = default);
    Task<Dictionary<int, int>> GetServerIdsForTasksAsync(IReadOnlyCollection<int> taskIds, CancellationToken ct = default);
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

    /// <summary>
    /// Item #7: in-flight tasks (Pending/Assigned/Running) visible to the caller. Used by the
    /// top-bar tracker on (re)connect to recover the current state without paginating through
    /// the full history. Returns at most a reasonable cap (200) - past that, the UI defers to
    /// the dedicated tasks page.
    /// </summary>
    Task<List<ServerTaskDto>> GetActiveTasksAsync(List<int>? accessibleServerIds, CancellationToken ct = default);

    /// <summary>The server names across the caller's tasks (optionally one server's).</summary>
    Task<TaskFilterValuesDto> GetTaskFilterValuesAsync(List<int>? accessibleServerIds, int? serverId, CancellationToken ct = default);
}
