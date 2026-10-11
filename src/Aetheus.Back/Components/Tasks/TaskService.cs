// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Tasks;

// IDomainEventDispatcher is REQUIRED, not optional. It used to default to null while an
// IPipelineTaskCompletionPort carried the orchestration call; now the events ARE the orchestration
// call, so a null dispatcher would silently strand every run mid-stage while every task reported
// success.
public class TaskService(ITaskRepository repo, ILogService logService, IHubContext<PipelineHub> pipelineHub, IHubContext<ServerHub> serverHub, IAuditService audit, TimeProvider timeProvider, IEncryptionService encryption, IArtifactService artifactService, ITaskQueueNotifier taskQueueNotifier, ILogger<TaskService> logger, IDomainEventDispatcher domainEvents) : ITaskService
{
    public Task<PaginatedResult<ServerTaskDto>> GetTasksAsync(TaskPaginationRequest request, CancellationToken ct = default)
        => GetTasksAsync(request, null, ct);

    public async Task<PaginatedResult<ServerTaskDto>> GetTasksAsync(TaskPaginationRequest request, List<int>? accessibleServerIds = null, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetTasksPagedAsync(
            request.Search, page, pageSize, request.Status, accessibleServerIds, request.ServerId, ct,
            request.SortBy, request.SortDescending, request.Filters).ConfigureAwait(false);

        return new PaginatedResult<ServerTaskDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<TaskFilterValuesDto> GetTaskFilterValuesAsync(List<int>? accessibleServerIds, int? serverId, CancellationToken ct = default)
        => repo.GetTaskFilterValuesAsync(accessibleServerIds, serverId, ct);

    public async Task<ServerTaskDto?> GetTaskAsync(int id, CancellationToken ct = default)
    {
        var task = await repo.GetTaskWithServerAsync(id, ct).ConfigureAwait(false);
        return task is null ? null : MapToDto(task);
    }

    public async Task<List<ServerTaskDto>> GetActiveTasksAsync(List<int>? accessibleServerIds, CancellationToken ct = default)
    {
        // Single query for all active statuses instead of three sequential queries.
        // The widget can show ~50 lines; the cap of 200 leaves room for a SaaS-scale
        // fan-out while keeping the JSON payload bounded.
        var activeStatuses = new List<TaskExecutionStatus>
        {
            TaskExecutionStatus.Running,
            TaskExecutionStatus.Assigned,
            TaskExecutionStatus.Pending
        };
        var (items, _) = await repo.GetTasksByStatusesPagedAsync(null, 1, 200, activeStatuses, accessibleServerIds, ct).ConfigureAwait(false);

        // Most-recent first - visually matches the widget popover (newest at top).
        return items
            .OrderByDescending(t => t.CreatedAt)
            .Take(200)
            .Select(MapToDto)
            .ToList();
    }

    // A task can only run if an agent on the target server is polling to claim it. Refuse to queue
    // one for an offline server so it does not sit Pending forever with no logs (the apache2-install
    // confusion). The module services that persist through their own repository reach it through
    // TaskQueuePersistence (SITE-OFFLINE-001); the server-management collaborator has its own check and
    // the agent's own paths bypass it on purpose.
    public async Task EnsureServerOnlineAsync(int serverId, CancellationToken ct = default)
    {
        var status = await repo.GetServerStatusAsync(serverId, ct).ConfigureAwait(false);
        if (status is not null && status != ServerStatus.Online)
            throw new ConflictException(
                "The target server's agent is offline, so this task cannot run. Reconnect the agent and try again.");
    }

    public async Task<ServerTaskDto> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        await EnsureServerOnlineAsync(request.ServerId, ct).ConfigureAwait(false);
        var task = new ServerTask
        {
            ServerId = request.ServerId,
            Name = request.Name,
            Command = request.Command,
            Executor = request.Executor,
            TimeoutSeconds = request.TimeoutSeconds,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(request.EnvironmentVariables))
        };
        task = await repo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "Task", task.Id, task.Name, ct).ConfigureAwait(false);
        var dto = MapToDto(task);
        await taskQueueNotifier.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);
        return dto;
    }

    public async Task<ServerTaskDto> CreateOperationAsync(CreateOperationRequest request, CancellationToken ct = default)
    {
        await EnsureServerOnlineAsync(request.ServerId, ct).ConfigureAwait(false);

        // F-32: typed operations carry their target string in `Command`. The agent's
        // IOperationExecutor validates the target against a per-kind regex, so no
        // shell allow-list is involved.
        var task = new ServerTask
        {
            ServerId = request.ServerId,
            Name = request.Name,
            Command = request.Target,
            Executor = ExecutorType.Operation, // Hardening (#30): typed operation, not shell
            Operation = request.Operation,
            TimeoutSeconds = request.TimeoutSeconds,
            EnvironmentVariables = "{}"
        };
        task = await repo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await audit.LogAsync("CreatedOperation", "Task", task.Id, $"{request.Operation}:{request.Target}", ct).ConfigureAwait(false);
        var dto = MapToDto(task);
        await taskQueueNotifier.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);
        return dto;
    }

    // Broadcast hook for tasks queued outside this service (ServerServiceManager's service-action and
    // package install/uninstall paths persist straight through the repository). Maps the same way and
    // reuses the same per-server/all-servers fan-out so the top-bar tracker treats them identically.
    // serverNameOverride: callers whose task entity has no Server navigation loaded (e.g. bulk
    // queuing from id/name pairs) pass the name explicitly so the tracker line stays readable.
    public Task NotifyTaskQueuedAsync(
        ServerTask task,
        string? serverNameOverride = null,
        CancellationToken ct = default) =>
        taskQueueNotifier.NotifyTaskQueuedAsync(task, serverNameOverride, ct);

    public async Task<List<PendingTaskDto>> GetPendingTasksAsync(
        int serverId,
        int? take = null,
        string? agentSessionId = null,
        CancellationToken ct = default)
    {
        var tasks = await repo.ClaimPendingTasksAsync(serverId, take, agentSessionId, ct).ConfigureAwait(false);

        return tasks.Select(t => new PendingTaskDto
        {
            Id = t.Id,
            AgentSessionId = t.AssignedAgentSessionId ?? string.Empty,
            AgentSessionFencingToken = t.AssignedAgentSessionFencingToken ?? 0,
            PipelineRunId = t.PipelineRunId,
            PurgeWorkspace = t.PipelineStepRun is
            { IsSystem: true, StageName: PipelineSystemStages.Cleanup },
            Name = t.Name,
            Command = t.Command,
            Executor = t.Executor,
            Operation = t.Operation,
            TimeoutSeconds = t.TimeoutSeconds,
            EnvironmentVariables = DeserializeEnv(TaskEnvProtection.Unprotect(encryption, t.EnvironmentVariables)),
            Container = t.ContainerImage is null && t.ContainerToolchain is null ? null : new ContainerSpec
            {
                Image = t.ContainerImage ?? string.Empty,
                Toolchain = t.ContainerToolchain,
                Shell = t.ContainerShell,
                Runtime = t.ContainerRuntime,
                Network = t.ContainerNetwork,
                Memory = t.ContainerMemory,
                Cpus = t.ContainerCpus,
                WorkspaceKey = t.PipelineRunId ?? t.Id,
                CacheTrustDomain = t.PipelineRun?.Pipeline.Project is { } project
                    ? $"org-{project.OrganizationId}-project-{project.Id}"
                    : t.PipelineRun is { } run
                        ? $"pipeline-{run.PipelineId}"
                        : $"task-{t.Id}",
                // Only the run's system cleanup step purges the host workspace dir. IsSystem can't be
                // set by user steps, so a user step named "Cleanup" can never trigger a mid-run purge.
                PurgeWorkspace = t.PipelineStepRun is { IsSystem: true, StepName: "Cleanup" }
            }
        }).ToList();
    }

    /// <summary>
    /// Returns an assigned-but-never-started task to the queue at the agent's own request. An agent that
    /// claimed work it cannot begin right now (its local build lease is held by another task) would
    /// otherwise sit in Assigned until <c>AssignedStartTimeout</c> kills it, which makes two build
    /// pipelines on one runner mutually exclusive. Pending is the state the watchdog already treats as
    /// legitimate queue time while the server is Online, so handing the task back is the honest move.
    /// </summary>
    public async Task<bool> ReleaseAssignedTaskAsync(
        int id,
        AgentTaskLeaseRequest lease,
        CancellationToken ct = default)
    {
        var task = await repo.FindTaskAsync(id, ct).ConfigureAwait(false);
        if (task is null || task.Status is not TaskExecutionStatus.Assigned || task.StartedAt is not null)
            return false;

        if (!await repo.TryReleaseAssignedTaskWithLeaseAsync(
                task, lease.AgentSessionId, lease.AgentSessionFencingToken, ct).ConfigureAwait(false))
            return false;

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation(
            "Task {TaskId} on server {ServerId} was returned to the queue by its agent before starting",
            task.Id,
            task.ServerId);
        return true;
    }

    public Task<bool> StartTaskAsync(int id, CancellationToken ct = default) =>
        StartTaskCoreAsync(id, null, ct);

    public Task<bool> StartTaskAsync(int id, AgentTaskLeaseRequest lease, CancellationToken ct = default) =>
        StartTaskCoreAsync(id, lease, ct);

    private async Task<bool> StartTaskCoreAsync(
        int id,
        AgentTaskLeaseRequest? lease,
        CancellationToken ct)
    {
        var task = await repo.FindTaskAsync(id, ct).ConfigureAwait(false);
        if (task is null || task.Status is not (TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned))
            return false;

        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        if (lease is not null)
        {
            if (!await repo.TryStartTaskWithLeaseAsync(
                    task,
                    lease.AgentSessionId,
                    lease.AgentSessionFencingToken,
                    startedAt,
                    ct)
                .ConfigureAwait(false))
                return false;
        }
        else
        {
            if (!await repo.TryStartTaskAsync(task, startedAt, ct).ConfigureAwait(false))
                return false;
            logger.LogWarning(
                "Task {TaskId} on server {ServerId} started through the temporary N-1 unfenced protocol",
                task.Id,
                task.ServerId);
        }

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(
            serverHub.Clients.Group(HubGroups.Server(task.ServerId))
                .SendAsync("TaskStarted", new { TaskId = task.Id, ServerId = task.ServerId, StartedAt = task.StartedAt }, ct),
            serverHub.Clients.Group(HubGroups.AllServers)
                .SendAsync("TaskStarted", new { TaskId = task.Id, ServerId = task.ServerId, StartedAt = task.StartedAt }, ct)
        ).ConfigureAwait(false);

        if (task.PipelineRunId.HasValue)
            await pipelineHub.Clients.Group($"pipeline-run-{task.PipelineRunId}")
                .SendAsync("StepStarted", task.PipelineStepRunId, ct).ConfigureAwait(false);

        return true;
    }

    public async Task<bool> CompleteTaskAsync(int id, TaskResultDto result, CancellationToken ct = default)
    {
        var task = await repo.FindTaskAsync(id, ct).ConfigureAwait(false);
        if (task is null || !TerminalTaskStatuses.Contains(result.Status)) return false;

        // Compatibility guard for agents that predate the current lifecycle contract: an old agent
        // can discover that a newly introduced operation is unsupported and report a terminal failure
        // while the task is still Assigned. Accept only a non-success terminal result from Assigned;
        // successful work must still have passed through StartTaskAsync/Running.
        var canComplete = task.Status == TaskExecutionStatus.Running
            || (task.Status == TaskExecutionStatus.Assigned && result.Status != TaskExecutionStatus.Success);
        if (!canComplete) return false;

        var selfUpdateHandoff = await SelfUpdateTaskHandoff.TryAcknowledgeAsync(
            repo, timeProvider, task, result, ct).ConfigureAwait(false);
        if (selfUpdateHandoff.HasValue) return selfUpdateHandoff.Value;

        // Capture the deploy artifact id BEFORE the env is scrubbed below - a successful PipelineDeploy
        // closes the loop (retention + ReleaseStatus.Deployed) once the task is persisted.
        var deployClosure = TryReadDeployClosure(task, result.Status);
        var completedAt = timeProvider.GetUtcNow().UtcDateTime;
        if (result.AgentSessionId is not null && result.AgentSessionFencingToken is { } fencingToken)
        {
            if (!await repo.TryCompleteTaskWithLeaseAsync(
                    task,
                    result.AgentSessionId,
                    fencingToken,
                    result.Status,
                    result.ExitCode,
                    completedAt,
                    TaskEnvProtection.EmptyEnv,
                    ct)
                .ConfigureAwait(false))
                return false;
        }
        else
        {
            logger.LogWarning(
                "Task {TaskId} on server {ServerId} completed through the temporary N-1 unfenced protocol",
                task.Id,
                task.ServerId);
            task.Status = result.Status;
            task.ExitCode = result.ExitCode;
            task.CompletedAt = completedAt;
            task.EnvironmentVariables = TaskEnvProtection.EmptyEnv; // F-001: scrub secrets at terminal state
        }

        PipelineTaskFailureClassifier.ApplyReported(task, result, completedAt);

        var completionFinalizer = new TaskCompletionFinalizer(
            repo, logService, audit, timeProvider, artifactService, logger, domainEvents);
        var stepRun = await completionFinalizer.UpdateStepRunAsync(task, result, id, ct).ConfigureAwait(false);

        PipelineTaskFailureClassifier.ApplyIfMissing(task, stepRun, result, completedAt);

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await completionFinalizer.AuditDeferredCleanupAsync(task).ConfigureAwait(false);
        // From this point the agent result is durable and the encrypted retry context is scrubbed.
        // Finish the state machine even if the HTTP caller disconnects; otherwise a terminal task can
        // never be submitted again and its pipeline run remains permanently active.
        var completionCt = CancellationToken.None;

        // Deploy-success closure: flag the exact deployed release and retain its artifact. A bookkeeping
        // failure must become a terminal failed step; otherwise the already-scrubbed terminal task could
        // never be retried and the pipeline would remain stuck forever.
        if (deployClosure is { } closure)
            await completionFinalizer.CloseDeploymentAsync(
                task, stepRun, closure, completionCt).ConfigureAwait(false);

        // Advance the authoritative state machine before best-effort UI notifications. A SignalR
        // outage must not strand a terminal task between stages.
        await AdvancePipelineAfterCompletionAsync(task, stepRun, completionCt).ConfigureAwait(false);
        await BroadcastTaskCompletionAsync(task, completionCt).ConfigureAwait(false);
        return true;
    }

    private async Task AdvancePipelineAfterCompletionAsync(
        ServerTask task, PipelineStepRun? stepRun, CancellationToken ct)
    {
        if (task.PipelineRunId is not { } runId) return;
        if (stepRun is null && task.Operation != OperationKind.PipelineCollectArtifacts) return;

        await RaiseStepTaskCompletedAsync(runId, stepRun, task, task.Status, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Announces a settled pipeline task instead of calling the orchestrator. Strict dispatch, so a
    /// handler failure reaches this caller exactly as the direct call's exception did - a run left
    /// mid-stage must not read as a task that completed cleanly.
    /// </summary>
    private Task RaiseStepTaskCompletedAsync(
        int pipelineRunId, PipelineStepRun? stepRun, ServerTask task, TaskExecutionStatus status, CancellationToken ct) =>
        domainEvents.DispatchStrictAsync(
            new PipelineStepTaskCompletedEvent(
                pipelineRunId,
                stepRun?.StageName,
                status,
                task.Operation == OperationKind.PipelineCollectArtifacts),
            ct);

    private async Task BroadcastTaskCompletionAsync(ServerTask task, CancellationToken ct)
    {
        var notification = new TaskCompletedNotification
        {
            TaskId = task.Id,
            ServerId = task.ServerId,
            TaskName = task.Name,
            Status = task.Status,
            ExitCode = task.ExitCode,
            Output = null,
            Operation = task.Operation
        };
        try
        {
            await Task.WhenAll(
                serverHub.Clients.Group(HubGroups.Server(task.ServerId))
                    .SendAsync("TaskCompleted", notification, ct),
                serverHub.Clients.Group(HubGroups.AllServers)
                    .SendAsync("TaskCompleted", notification, ct)
            ).ConfigureAwait(false);

            if (task.PipelineRunId.HasValue)
                await pipelineHub.Clients.Group($"pipeline-run-{task.PipelineRunId}")
                    .SendAsync("StepCompleted", task.PipelineStepRunId, task.Status, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task completion notification failed for task {TaskId}", task.Id);
        }
    }

    public async Task<bool> ReportDeploymentBuildRefusalAsync(
        int id, DeploymentBuildRefusalReport report, CancellationToken ct = default)
    {
        if (report.TaskId != id
            || report.IncidentId == Guid.Empty
            || string.IsNullOrWhiteSpace(report.Reason))
            return false;

        var task = await repo.FindTaskAsync(id, ct).ConfigureAwait(false);
        if (task is null) return false;
        if (task.Status == TaskExecutionStatus.Failed) return true;
        if (task.Status is not TaskExecutionStatus.Assigned and not TaskExecutionStatus.Running)
            return false;

        return await CompleteTaskAsync(id, new TaskResultDto
        {
            TaskId = id,
            Status = TaskExecutionStatus.Failed,
            ExitCode = -1,
            FailureCode = "BuildOnDeploymentTarget",
            FailureReason = report.Reason
        }, ct).ConfigureAwait(false);
    }

    // A successful deploy task carries the artifact id + app name in its (still-encrypted) env; decode
    // them so the caller can close the deploy loop after the env is scrubbed. The app name is the
    // deployment cohort key for retention (all deploys of app X age out the previous one). Null unless
    // this is a PipelineDeploy task that actually succeeded.
    private DeployClosure? TryReadDeployClosure(ServerTask task, TaskExecutionStatus status)
    {
        if (task.Operation != OperationKind.PipelineDeploy || status != TaskExecutionStatus.Success) return null;
        try
        {
            var json = TaskEnvProtection.Unprotect(encryption, task.EnvironmentVariables);
            var env = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (env is null
                || !env.TryGetValue("AETHEUS_DEPLOY_ARTIFACT_ID", out var idStr)
                || !int.TryParse(idStr, out var artifactId))
                return null;
            var cohort = env.TryGetValue("AETHEUS_DEPLOY_APP", out var app) && !string.IsNullOrWhiteSpace(app)
                ? app
                : task.Command;
            var rollbackId = env.TryGetValue("AETHEUS_ROLLBACK_ID", out var rollbackIdText)
                && int.TryParse(rollbackIdText, out var parsedRollbackId)
                ? parsedRollbackId
                : (int?)null;
            var releaseId = env.TryGetValue("AETHEUS_DEPLOY_RELEASE_ID", out var releaseIdText)
                && int.TryParse(releaseIdText, out var parsedReleaseId)
                ? parsedReleaseId
                : (int?)null;
            return new DeployClosure(artifactId, cohort, rollbackId, releaseId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Cohort = the deployment cohort key (app name), NOT a promote environment. It is stored on the
    // artifact under RetentionPolicy.Deployed, which keeps it separate from promote environments even
    // when an app happens to be named like one.

    private static readonly TaskExecutionStatus[] TerminalTaskStatuses =
        [TaskExecutionStatus.Success, TaskExecutionStatus.Failed, TaskExecutionStatus.Cancelled, TaskExecutionStatus.Timeout];

    public async Task<bool> CancelTaskAsync(int id, CancellationToken ct = default)
    {
        var task = await repo.FindTaskAsync(id, ct).ConfigureAwait(false);
        // Reject EVERY terminal status (not just Success/Failed): a task already Cancelled or
        // Timeout (e.g. just swept by TaskTimeoutService) must not be re-cancelled - that would
        // re-broadcast StepCompleted and re-enter AdvanceStageAsync, double-advancing the stage.
        if (task is null || TerminalTaskStatuses.Contains(task.Status))
            return false;

        task.Status = TaskExecutionStatus.Cancelled;
        task.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
        task.EnvironmentVariables = TaskEnvProtection.EmptyEnv; // F-001: scrub secrets at terminal state

        // Mirror CompleteTaskAsync's pipeline plumbing: without it, cancelling a step's task
        // via the Tasks API left the PipelineStepRun Running and the run stuck In Progress,
        // with no SignalR notification to the top-bar tracker or the run view.
        // stepTransitioned = did THIS cancel actually move the step to terminal? If the step was
        // already completed (by a racing CompleteTaskAsync), we must NOT re-broadcast or re-advance.
        PipelineStepRun? stepRun = null;
        var stepTransitioned = false;
        if (task.PipelineStepRunId.HasValue)
        {
            stepRun = await repo.FindPipelineStepRunAsync(task.PipelineStepRunId.Value, ct).ConfigureAwait(false);
            if (stepRun is not null && stepRun.CompletedAt is null)
            {
                stepRun.Status = TaskExecutionStatus.Cancelled;
                stepRun.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
                stepTransitioned = true;
            }
        }

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        var notification = new TaskCompletedNotification
        {
            TaskId = task.Id,
            ServerId = task.ServerId,
            TaskName = task.Name,
            Status = TaskExecutionStatus.Cancelled,
            ExitCode = task.ExitCode,
            Output = null,
            Operation = task.Operation
        };
        await Task.WhenAll(
            serverHub.Clients.Group(HubGroups.Server(task.ServerId))
                .SendAsync("TaskCompleted", notification, ct),
            serverHub.Clients.Group(HubGroups.AllServers)
                .SendAsync("TaskCompleted", notification, ct)
        ).ConfigureAwait(false);

        // Only touch the pipeline run when THIS call actually transitioned the step - otherwise a
        // concurrent completion already broadcast StepCompleted and advanced the stage.
        if (task.PipelineRunId.HasValue && stepTransitioned)
        {
            await pipelineHub.Clients.Group($"pipeline-run-{task.PipelineRunId}")
                .SendAsync("StepCompleted", task.PipelineStepRunId, TaskExecutionStatus.Cancelled, ct).ConfigureAwait(false);
            await RaiseStepTaskCompletedAsync(
                task.PipelineRunId.Value, stepRun, task, TaskExecutionStatus.Cancelled, ct).ConfigureAwait(false);
        }
        else if (task.PipelineRunId.HasValue && stepRun is null && task.Operation == OperationKind.PipelineCollectArtifacts)
        {
            await RaiseStepTaskCompletedAsync(
                task.PipelineRunId.Value, null, task, TaskExecutionStatus.Cancelled, ct).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<int?> GetTaskServerIdAsync(int taskId, CancellationToken ct = default)
    {
        var task = await repo.FindTaskAsync(taskId, ct).ConfigureAwait(false);
        return task?.ServerId;
    }

    public async Task<bool> AllowsLegacyUnfencedTaskProtocolAsync(
        int serverId,
        CancellationToken ct = default)
    {
        var agentVersion = await repo.GetServerAgentVersionAsync(serverId, ct).ConfigureAwait(false);
        return AgentTaskProtocolCompatibility.AllowsLegacyUnfencedRequests(agentVersion);
    }

    public Task<Dictionary<int, int>> GetServerIdsForTasksAsync(IReadOnlyCollection<int> taskIds, CancellationToken ct = default) =>
        repo.GetServerIdsForTasksAsync(taskIds, ct);

    public Task<bool> HasCurrentAgentLeaseAsync(
        int taskId,
        int serverId,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default) =>
        repo.HasCurrentAgentLeaseAsync(taskId, serverId, agentSessionId, fencingToken, ct);

    public Task<bool> HaveCurrentAgentLeasesAsync(
        IReadOnlyCollection<int> taskIds,
        int serverId,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default) =>
        repo.HaveCurrentAgentLeasesAsync(taskIds, serverId, agentSessionId, fencingToken, ct);

    public async Task<Dictionary<int, string>> GetTaskStatusesAsync(List<int> taskIds, int serverId, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return new();
        return await repo.GetTaskStatusesAsync(taskIds, serverId, ct).ConfigureAwait(false);
    }

    private static ServerTaskDto MapToDto(ServerTask task) =>
        TaskDtoMapper.ToMaskedDto(task);

    private static Dictionary<string, string> DeserializeEnv(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        // Malformed env JSON in legacy rows degrades to empty env (intentional, validated on write).
        catch (JsonException) { return []; }
    }
}
