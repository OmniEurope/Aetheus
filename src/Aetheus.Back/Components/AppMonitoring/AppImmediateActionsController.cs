// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// R-29: the immediate probe and the immediate telemetry export of a monitored application, from its Supervision.
/// Triggering one needs Write on the application's project, the right that edits the application (the organization
/// check is part of it: a role reaches only the projects its organization owns); reading where one stands needs Read.
/// </summary>
[ApiController]
[Route("api/appmonitoring")]
[Authorize]
public sealed class AppImmediateActionsController(
    IAppImmediateActionService actions,
    IAppMonitoringService monitoring,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpPost("apps/{id:int}/probe-now")]
    public async Task<ActionResult<AppProbeNowDto>> ProbeNow(int id, CancellationToken ct)
    {
        if (await AuthorizeAsync(id, Permission.Write, ct) is { } refused)
            return refused;
        var result = await actions.ProbeNowAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("apps/{id:int}/probe-now")]
    public async Task<ActionResult<AppProbeNowDto>> GetProbeNow(int id, CancellationToken ct)
    {
        if (await AuthorizeAsync(id, Permission.Read, ct) is { } refused)
            return refused;
        var result = await actions.GetProbeNowAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("apps/{id:int}/telemetry-export-request")]
    public async Task<ActionResult<AppTelemetryExportRequestDto>> RequestTelemetryExport(int id, CancellationToken ct)
    {
        if (await AuthorizeAsync(id, Permission.Write, ct) is { } refused)
            return refused;
        var result = await actions.RequestTelemetryExportAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("apps/{id:int}/telemetry-export-request")]
    public async Task<ActionResult<AppTelemetryExportRequestDto>> GetTelemetryExportRequest(int id, CancellationToken ct)
    {
        if (await AuthorizeAsync(id, Permission.Read, ct) is { } refused)
            return refused;
        var result = await actions.GetTelemetryExportRequestAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    private async Task<ActionResult?> AuthorizeAsync(int appId, Permission required, CancellationToken ct)
    {
        var projectId = await monitoring.GetAppProjectIdAsync(appId, ct);
        if (projectId is null)
            return NotFound();
        return await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, required, ct) ? null : Forbid();
    }
}
