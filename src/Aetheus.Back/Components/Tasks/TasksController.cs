// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Tasks;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TasksController(ITaskService taskService, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<ServerTaskDto>>> GetTasks([FromQuery] TaskPaginationRequest request, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Server, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<ServerTaskDto>());
        return Ok(await taskService.GetTasksAsync(request, accessibleIds, ct));
    }

    /// <summary>The server names the task list's Server column filter offers.</summary>
    [HttpGet("filter-values")]
    public async Task<ActionResult<TaskFilterValuesDto>> GetTaskFilterValues([FromQuery] int? serverId, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Server, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new TaskFilterValuesDto());
        return Ok(await taskService.GetTaskFilterValuesAsync(accessibleIds, serverId, ct));
    }

    /// <summary>
    /// Item #7: returns the in-flight tasks (Pending/Assigned/Running) the caller can see.
    /// Used by the top-bar task tracker on (re)connect - SignalR alone could miss events when
    /// the client is just opening or has been disconnected.
    /// </summary>
    [HttpGet("active")]
    public async Task<ActionResult<List<ServerTaskDto>>> GetActiveTasks(CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Server, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new List<ServerTaskDto>());
        return Ok(await taskService.GetActiveTasksAsync(accessibleIds, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ServerTaskDto>> GetTask(int id, CancellationToken ct)
    {
        var serverId = await taskService.GetTaskServerIdAsync(id, ct);
        if (serverId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId.Value, Permission.Read, ct))
            return Forbid();

        var task = await taskService.GetTaskAsync(id, ct);
        if (task is null) return NotFound();
        return Ok(task);
    }

    [HttpPost]
    public async Task<ActionResult<ServerTaskDto>> CreateTask([FromBody] CreateTaskRequest request, CancellationToken ct)
    {
        // F-15: a free-form command is effectively arbitrary code execution on the target server.
        // Require Server Admin (the strongest permission) instead of plain Write so users with
        // ordinary edit rights cannot escalate to RCE. Typed operations (CreateOperation below)
        // remain Write-gated because their surface is constrained by OperationTargetValidator.
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, request.ServerId, Permission.Admin, ct))
            return Forbid();

        var task = await taskService.CreateTaskAsync(request, ct);
        return CreatedAtAction(nameof(GetTask), new { id = task.Id }, task);
    }

    /// <summary>
    /// F-32: enqueue a typed operation. Bypasses the free-form shell allow-list - the agent
    /// dispatches by <c>Operation</c> via a dedicated <c>IOperationExecutor</c>.
    /// </summary>
    [HttpPost("operation")]
    public async Task<ActionResult<ServerTaskDto>> CreateOperation([FromBody] CreateOperationRequest request, CancellationToken ct)
    {
        if (request.Operation == OperationKind.None)
            return BadRequest(new { error = "InvalidOperation", message = "Operation kind is required" });

        // Server-side defence-in-depth: enforce the same per-kind regex the agent uses.
        // Single source of truth lives in Aetheus.Shared.Components.Shared.OperationTargetValidator.
        if (!OperationTargetValidator.IsValid(request.Operation, request.Target))
            return BadRequest(new { error = "InvalidTarget", message = "Operation target format is invalid for the selected kind" });

        if (!await authz.HasPermissionAsync(User, ResourceType.Server, request.ServerId, Permission.Write, ct))
            return Forbid();

        var task = await taskService.CreateOperationAsync(request, ct);
        return CreatedAtAction(nameof(GetTask), new { id = task.Id }, task);
    }

    [HttpPost("statuses")]
    [Authorize(Policy = "AgentToken")]
    public async Task<ActionResult<Dictionary<int, string>>> GetTaskStatuses([FromBody] List<int> taskIds, CancellationToken ct)
    {
        if (taskIds.Count > 100) return BadRequest();
        var claim = User.FindFirst("ServerId")?.Value;
        if (claim is null || !int.TryParse(claim, out var agentServerId))
            return Forbid();
        var statuses = await taskService.GetTaskStatusesAsync(taskIds, agentServerId, ct);
        return Ok(statuses);
    }

    [HttpPost("claim")]
    [Authorize(Policy = "AgentToken")]
    public async Task<ActionResult<List<PendingTaskDto>>> ClaimPendingTasks(
        [FromQuery] int serverId,
        [FromQuery] int? take,
        [FromQuery] string? agentSessionId,
        CancellationToken ct)
    {
        if (!IsAgentAuthorizedForServer(serverId))
            return Forbid();
        if (agentSessionId is null
            || agentSessionId.Length > 64
            || !Guid.TryParseExact(agentSessionId, "N", out _))
            return BadRequest("agentSessionId must be a 32-character GUID.");

        // take = the agent's free execution slots (absent on older agents = claim all).
        return Ok(await taskService.GetPendingTasksAsync(serverId, take, agentSessionId, ct));
    }

    /// <summary>
    /// The agent hands back work it claimed but cannot begin yet, so the task waits in Pending (which the
    /// watchdog accepts while the runner is Online) instead of ageing out of Assigned in two minutes.
    /// Requires a fenced lease: without proof of ownership, a caller could unqueue someone else's work.
    /// </summary>
    [HttpPost("{id:int}/release")]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> ReleaseTask(
        int id,
        [FromBody] AgentTaskLeaseRequest lease,
        CancellationToken ct)
    {
        var taskServerId = await taskService.GetTaskServerIdAsync(id, ct);
        if (taskServerId is null) return NotFound();
        if (!IsAgentAuthorizedForServer(taskServerId.Value))
            return Forbid();
        if (!Guid.TryParseExact(lease.AgentSessionId, "N", out _) || lease.AgentSessionFencingToken <= 0)
            return BadRequest();

        return await taskService.ReleaseAssignedTaskAsync(id, lease, ct)
            ? Ok()
            : Conflict();
    }

    [HttpPost("{id:int}/start")]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> StartTask(
        int id,
        [FromBody] AgentTaskLeaseRequest lease,
        CancellationToken ct)
    {
        var taskServerId = await taskService.GetTaskServerIdAsync(id, ct);
        if (taskServerId is null) return NotFound();
        if (!IsAgentAuthorizedForServer(taskServerId.Value))
            return Forbid();
        var hasFencedLease = Guid.TryParseExact(lease.AgentSessionId, "N", out _)
            && lease.AgentSessionFencingToken > 0;
        var legacyAllowed = !hasFencedLease
            && await taskService.AllowsLegacyUnfencedTaskProtocolAsync(taskServerId.Value, ct);
        if (!hasFencedLease && !legacyAllowed)
            return BadRequest();

        var success = hasFencedLease
            ? await taskService.StartTaskAsync(id, lease, ct)
            : await taskService.StartTaskAsync(id, ct);
        if (!success) return Conflict();
        return Ok();
    }

    [HttpPost("{id:int}/complete")]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> CompleteTask(int id, [FromBody] TaskResultDto result, CancellationToken ct)
    {
        var taskServerId = await taskService.GetTaskServerIdAsync(id, ct);
        if (taskServerId is null) return NotFound();
        if (!IsAgentAuthorizedForServer(taskServerId.Value))
            return Forbid();
        var hasFencedLease = result.AgentSessionId is not null
            && Guid.TryParseExact(result.AgentSessionId, "N", out _)
            && result.AgentSessionFencingToken is > 0;
        if (!hasFencedLease
            && !await taskService.AllowsLegacyUnfencedTaskProtocolAsync(taskServerId.Value, ct))
            return BadRequest();

        var success = await taskService.CompleteTaskAsync(id, result, ct);
        if (!success) return Conflict();
        return Ok();
    }

    [HttpPost("{id:int}/deployment-build-refusal")]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> ReportDeploymentBuildRefusal(
        int id,
        [FromBody] DeploymentBuildRefusalReport report,
        CancellationToken ct)
    {
        var taskServerId = await taskService.GetTaskServerIdAsync(id, ct);
        if (taskServerId is null) return NotFound();
        if (!IsAgentAuthorizedForServer(taskServerId.Value)) return Forbid();

        var success = await taskService.ReportDeploymentBuildRefusalAsync(id, report, ct);
        return success ? Ok() : Conflict();
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> CancelTask(int id, CancellationToken ct)
    {
        var serverId = await taskService.GetTaskServerIdAsync(id, ct);
        if (serverId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId.Value, Permission.Write, ct))
            return Forbid();

        var success = await taskService.CancelTaskAsync(id, ct);
        if (!success) return NotFound();
        return Ok();
    }

    private bool IsAgentAuthorizedForServer(int serverId)
    {
        var claim = User.FindFirst("ServerId")?.Value;
        return claim is not null && int.TryParse(claim, out var agentServerId) && agentServerId == serverId;
    }
}
