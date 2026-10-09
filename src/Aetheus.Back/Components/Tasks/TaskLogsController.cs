// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Logs;

namespace Aetheus.Back.Components.Tasks;

[ApiController]
// Pinned, not derived from the class name: the agent protocol publishes `api/logs` (ADR-033) and
// the controller moved modules, so `[controller]` would have silently renamed a wire contract.
[Route("api/logs")]
[Authorize]
public class TaskLogsController(
    ILogService logService,
    ITaskService taskService,
    IResourceAuthorizationService authz,
    IAuditService audit) : ControllerBase
{
    [HttpGet("task/{taskId:int}")]
    public async Task<ActionResult<List<TaskLogDto>>> GetTaskLogs(int taskId, [FromQuery] int? maxLines, CancellationToken ct)
    {
        var serverId = await taskService.GetTaskServerIdAsync(taskId, ct);
        if (serverId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId.Value, Permission.Read, ct))
            return Forbid();

        return Ok(await logService.GetTaskLogsAsync(taskId, maxLines, ct));
    }

    /// <summary>Recette R2-104: a page of the task's complete log, from its first line (pageSize 1 to 200).</summary>
    [HttpGet("task/{taskId:int}/page")]
    public async Task<ActionResult<PaginatedResult<TaskLogDto>>> GetTaskLogPage(
        int taskId, [FromQuery] int page = 1, [FromQuery] int pageSize = 200, CancellationToken ct = default)
    {
        var serverId = await taskService.GetTaskServerIdAsync(taskId, ct);
        if (serverId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId.Value, Permission.Read, ct))
            return Forbid();

        return Ok(await logService.GetTaskLogPageAsync(taskId, page, Math.Clamp(pageSize, 1, 200), ct));
    }

    [HttpGet("task/{taskId:int}/unmasked")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<TaskLogDto>>> GetTaskLogsUnmasked(int taskId, [FromQuery] int? maxLines, CancellationToken ct)
    {
        var serverId = await taskService.GetTaskServerIdAsync(taskId, ct);
        if (serverId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId.Value, Permission.Admin, ct))
            return Forbid();

        // Dedicated audit trail for high-sensitivity unmasked-secret access.
        await audit.LogAsync("AccessedUnmasked", "TaskLogs", taskId, $"Server={serverId.Value}", ct);

        return Ok(await logService.GetTaskLogsUnmaskedAsync(taskId, maxLines, ct));
    }

    [HttpPost]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> AppendLog([FromBody] AppendLogRequest request, CancellationToken ct)
    {
        var agentServerId = GetAgentServerId();
        if (agentServerId is null)
            return Forbid();
        var taskServerId = await taskService.GetTaskServerIdAsync(request.TaskId, ct);
        if (taskServerId != agentServerId)
            return Forbid();

        var hasLeasePayload = HasValidLease(request);
        var hasCurrentLease = hasLeasePayload
            && await taskService.HasCurrentAgentLeaseAsync(
                request.TaskId,
                agentServerId.Value,
                request.AgentSessionId!,
                request.AgentSessionFencingToken!.Value,
                ct);
        if (hasLeasePayload && !hasCurrentLease)
            return Conflict();
        if (!hasLeasePayload
            && !await taskService.AllowsLegacyUnfencedTaskProtocolAsync(agentServerId.Value, ct))
            return Conflict();

        await logService.AppendLogAsync(request, ct);
        return Ok();
    }

    [HttpPost("batch")]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> AppendLogs([FromBody] List<AppendLogRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0)
            return Ok();

        // Cap batch size to prevent DoS via huge agent payloads.
        if (requests.Count > 500)
            return BadRequest(new ApiError { Message = "Batch size exceeds maximum of 500 entries." });

        var claim = User.FindFirst("ServerId")?.Value;
        if (claim is null || !int.TryParse(claim, out var agentServerId))
            return Forbid();

        // Hardening (#47): single batched lookup instead of N+1 per-line auth queries.
        // Use a HashSet directly to avoid the LINQ Distinct() + ToList() double allocation
        // on every batch.
        var distinctIds = new HashSet<int>(requests.Count);
        foreach (var r in requests) distinctIds.Add(r.TaskId);
        var taskServerIds = await taskService.GetServerIdsForTasksAsync(distinctIds, ct);
        foreach (var id in distinctIds)
        {
            if (!taskServerIds.TryGetValue(id, out var sid) || sid != agentServerId)
                return Forbid();
        }
        var first = requests[0];
        var hasLeasePayload = HasValidLease(first)
            && !requests.Any(request =>
                request.AgentSessionId != first.AgentSessionId
                || request.AgentSessionFencingToken != first.AgentSessionFencingToken);
        var hasCurrentLease = hasLeasePayload
            && await taskService.HaveCurrentAgentLeasesAsync(
                distinctIds,
                agentServerId,
                first.AgentSessionId!,
                first.AgentSessionFencingToken!.Value,
                ct);
        if (hasLeasePayload && !hasCurrentLease)
            return Conflict();
        if (!hasLeasePayload
            && !await taskService.AllowsLegacyUnfencedTaskProtocolAsync(agentServerId, ct))
            return Conflict();

        await logService.AppendLogsAsync(requests, ct);
        return Ok();
    }

    private int? GetAgentServerId()
    {
        var claim = User.FindFirst("ServerId")?.Value;
        return claim is not null && int.TryParse(claim, out var serverId) ? serverId : null;
    }

    private static bool HasValidLease(AppendLogRequest request) =>
        request.AgentSessionId is not null
        && Guid.TryParseExact(request.AgentSessionId, "N", out _)
        && request.AgentSessionFencingToken is > 0;
}
