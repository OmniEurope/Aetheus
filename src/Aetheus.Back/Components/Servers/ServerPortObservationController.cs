// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.PortRegistry;

namespace Aetheus.Back.Components.Servers;

/// <summary> The two ends of a port scan. Asking for one is a task queued on the agent (<see
/// cref="OperationKind.PortsObserve"/>); the result comes back on the agent's own token. It lives
/// in <c>Servers</c> and not in <c>PortRegistry</c> on purpose: queueing a task means reaching
/// <c>Tasks</c>, and the registry sits below it in the module layering. Servers is above both, so
/// it can hold the orchestration while the registry keeps being pure storage. </summary>
[ApiController]
[Route("api/servers/{serverId:int}/ports")]
// Class-level [Authorize] is the default deny; the agent-facing action overrides it with the
// AgentToken policy, which is narrower, not looser.
[Authorize]
public class ServerPortObservationController(
    IPortRegistryService registry,
    Tasks.ITaskService tasks,
    IServerLifecycleService servers,
    IResourceAuthorizationService authz,
    TimeProvider timeProvider,
    ILogger<ServerPortObservationController> logger) : ControllerBase
{
    /// <summary>
    /// Queues an on-demand scan. Read is enough: the scan changes nothing on the host, it only reads
    /// which sockets are listening, and refusing it to a reader would make the "is this port really
    /// free?" question unanswerable for exactly the people who ask it.
    /// </summary>
    [HttpPost("observe")]
    [Authorize]
    public async Task<ActionResult<ServerTaskDto>> Observe(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();

        var server = await servers.GetServerDetailAsync(serverId, ct);
        if (server is null) return NotFound();

        if (server.PortObservationAvailable is not true)
            return BadRequest(new ApiError
            {
                Message =
                    $"The agent on '{server.Name}' does not publish the '{AgentCapabilities.PortObservation}' "
                    + "capability, so it cannot scan the host's listening ports. Update the agent."
            });

        return Ok(await tasks.CreateOperationAsync(new CreateOperationRequest
        {
            ServerId = serverId,
            Name = "Observe listening ports",
            Operation = OperationKind.PortsObserve,
            Target = string.Empty,
            TimeoutSeconds = 60
        }, ct));
    }

    /// <summary>
    /// Where an agent pushes what its scan saw. Authorized by the agent token and scoped to the server
    /// that token belongs to, so an agent can never rewrite another host's observations.
    /// </summary>
    [HttpPost("observed")]
    [Authorize(Policy = "AgentToken")]
    [RequestSizeLimit(128 * 1024)]
    public async Task<IActionResult> ReportObserved(
        int serverId, [FromBody] ObservedPortsReportDto report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        var claim = User.FindFirst("ServerId")?.Value;
        if (claim is null || !int.TryParse(claim, out var agentServerId) || agentServerId != serverId)
            return Forbid();

        if (report.Ports.Count > PortRegistryLimits.MaxObservedPorts)
            return BadRequest(new ApiError
            {
                Message = $"A port observation carries at most {PortRegistryLimits.MaxObservedPorts} entries."
            });

        // The agent's own clock is not trusted for the stamp the UI shows as "last scan": a host with a
        // skewed clock would otherwise report a scan in the future or in the past.
        var observedAt = timeProvider.GetUtcNow().UtcDateTime;
        await registry.ReplaceObservedAsync(serverId, report.Ports, observedAt, ct);
        logger.LogDebug(
            "Server {ServerId} reported {Count} listening ports.", serverId, report.Ports.Count);
        return NoContent();
    }
}
