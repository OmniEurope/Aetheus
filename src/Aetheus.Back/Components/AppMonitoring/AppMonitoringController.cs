// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.AppMonitoring;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AppMonitoringController(IAppMonitoringService service, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet("projects/{projectId:int}/apps")]
    public async Task<ActionResult<List<MonitoredAppDto>>> GetAppsForProject(int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetAppsForProjectAsync(projectId, ct));
    }

    [HttpGet("apps/{id:int}")]
    public async Task<ActionResult<MonitoredAppDto>> GetApp(int id, CancellationToken ct)
    {
        var projectId = await service.GetAppProjectIdAsync(id, ct);
        if (projectId is null)
            return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Read, ct))
            return Forbid();
        var app = await service.GetAppAsync(id, ct);
        return app is null ? NotFound() : Ok(app);
    }

    [HttpGet("apps/{id:int}/samples")]
    public async Task<ActionResult<List<AppHealthSampleDto>>> GetSamples(
        int id, [FromQuery, System.ComponentModel.DataAnnotations.Range(1, 168)] int hours = 24, CancellationToken ct = default)
    {
        var projectId = await service.GetAppProjectIdAsync(id, ct);
        if (projectId is null)
            return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetSamplesAsync(id, hours, ct));
    }

    [HttpPost("projects/{projectId:int}/apps")]
    public async Task<ActionResult<MonitoredAppDto>> CreateApp(int projectId, [FromBody] CreateMonitoredAppRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Write, ct))
            return Forbid();
        if (!await MayProbeFromServerAsync(request.ServerId, ct))
            return Forbid();
        var app = await service.CreateAppAsync(projectId, request, ct);
        return CreatedAtAction(nameof(GetApp), new { id = app.Id }, app);
    }

    [HttpPut("apps/{id:int}")]
    public async Task<ActionResult<MonitoredAppDto>> UpdateApp(int id, [FromBody] UpdateMonitoredAppRequest request, CancellationToken ct)
    {
        var projectId = await service.GetAppProjectIdAsync(id, ct);
        if (projectId is null)
            return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Write, ct))
            return Forbid();
        if (!await MayProbeFromServerAsync(await service.GetProbeServerChangeAsync(id, request, ct), ct))
            return Forbid();
        var app = await service.UpdateAppAsync(id, request, ct);
        return app is null ? NotFound() : Ok(app);
    }

    // BACK-008: an agent-probed app makes that server's agent issue the requests, so choosing it (or
    // a new URL for it) needs Write on the server, not only on the project. Null means no agent target
    // is chosen: the backend probes the app itself behind its SSRF guard (create), or the update keeps
    // the approved server and URL; the unconditional Project/Write gate already covers both.
    private async Task<bool> MayProbeFromServerAsync(int? serverId, CancellationToken ct) =>
        serverId is not { } server
        || await authz.HasPermissionAsync(User, ResourceType.Server, server, Permission.Write, ct);

    [HttpDelete("apps/{id:int}")]
    public async Task<IActionResult> DeleteApp(int id, CancellationToken ct)
    {
        var projectId = await service.GetAppProjectIdAsync(id, ct);
        if (projectId is null)
            return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Admin, ct))
            return Forbid();
        return await service.DeleteAppAsync(id, ct) ? NoContent() : NotFound();
    }

    [HttpGet("summary")]
    public async Task<ActionResult<AppMonitoringSummaryDto>> GetSummary(CancellationToken ct)
    {
        var accessible = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Project, Permission.Read, ct);
        return Ok(await service.GetSummaryAsync(accessible, ct));
    }

    // --- Agent-facing endpoints (pull config + push results) ---

    [HttpGet("agent/probes")]
    [Authorize(Policy = "AgentToken")]
    public async Task<ActionResult<List<AppProbeConfigDto>>> GetProbes(CancellationToken ct)
    {
        if (!TryGetAgentServerId(out var serverId))
            return Forbid();
        return Ok(await service.GetProbeConfigsForServerAsync(serverId, ct));
    }

    [HttpPost("agent/probe-results")]
    [Authorize(Policy = "AgentToken")]
    [RequestSizeLimit(256 * 1024)]
    public async Task<IActionResult> ReportProbeResults([FromBody] List<AppProbeResultDto> results, CancellationToken ct)
    {
        if (results.Count == 0)
            return Ok();
        if (results.Count > 1000)
            return BadRequest(new ApiError { Message = "Batch size exceeds maximum of 1000 entries." });
        if (!TryGetAgentServerId(out var agentServerId))
            return Forbid();

        // Ownership guard: every reported app must belong to this agent's server (single batched lookup),
        // mirroring LogsController.AppendLogs. Rejects the whole batch on any foreign/off-fleet app id.
        var appIds = results.Select(r => r.MonitoredAppId).ToHashSet();
        var owners = await service.GetAppServerIdsAsync(appIds, ct);
        foreach (var id in appIds)
        {
            if (!owners.TryGetValue(id, out var ownerServerId) || ownerServerId != agentServerId)
                return Forbid();
        }

        var applied = await service.IngestProbeResultsAsync(results, ct);
        return Ok(new { Applied = applied });
    }

    private bool TryGetAgentServerId(out int serverId)
    {
        serverId = 0;
        var claim = User.FindFirst("ServerId")?.Value;
        return claim is not null && int.TryParse(claim, out serverId);
    }
}
