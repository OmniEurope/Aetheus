// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;

namespace Aetheus.Back.Components.AppBackups;

/// <summary>
/// ADR-024 4.3: orchestrated app-backup policies. Policies are project-owned; management is authorized
/// against the owning project's RBAC. Agent result callbacks use the AgentToken scheme + an IDOR guard.
/// </summary>
[ApiController]
[Route("api/backups")]
[Authorize]
public sealed class AppBackupsController(IBackupPolicyService service, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<BackupPolicyDto>>> GetPolicies(
        [FromQuery] PaginationRequest request, [FromQuery] int? projectId, CancellationToken ct)
    {
        var accessible = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Project, Permission.Read, ct);
        if (projectId.HasValue)
        {
            if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Read, ct))
                return Forbid();
            accessible = [projectId.Value];
        }
        return Ok(await service.GetPoliciesAsync(accessible, request, ct));
    }

    [HttpPost]
    public async Task<ActionResult<BackupPolicyDto>> Create([FromBody] CreateBackupPolicyRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, request.ProjectId, Permission.Write, ct))
            return Forbid();
        // The target server must ALSO be one the caller can manage - otherwise a project-only user could
        // aim a pg_dump/mysqldump (with attacker-supplied DbHost/creds) at any server in the fleet
        // (blind cross-tenant SSRF + piloting another org's agent). Gate on Server.Write like every other
        // fleet operation.
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, request.ServerId, Permission.Write, ct))
            return Forbid();
        // RBAC audit, finding 6: and the server sits in the project's organization, so a caller of two
        // organizations never points one's backups at the other's agent.
        if (!await authz.BelongsToProjectOrganizationAsync(request.ProjectId, ResourceType.Server, [request.ServerId], ct))
            return Forbid();
        return Ok(await service.CreateAsync(request, ct));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<BackupPolicyDto>> Update(int id, [FromBody] UpdateBackupPolicyRequest request, CancellationToken ct)
    {
        if (!await EnsurePolicyWriteAsync(id, ct)) return Forbid();
        // Project.Write may rename, reschedule or run the approved configuration; changing what the
        // agent connects to or reads (host, credentials, paths) needs Server.Write, as at creation.
        var server = await service.GetServerForAgentParameterChangeAsync(id, request, ct);
        if (server is not null
            && !await authz.HasPermissionAsync(User, ResourceType.Server, server.Value, Permission.Write, ct))
            return Forbid();
        var dto = await service.UpdateAsync(id, request, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (!await EnsurePolicyWriteAsync(id, ct)) return Forbid();
        return await service.DeleteAsync(id, ct) ? NoContent() : NotFound();
    }

    [HttpGet("{id:int}/runs")]
    public async Task<ActionResult<PaginatedResult<BackupRunDto>>> GetRuns(
        int id, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var projectId = await service.GetOwningProjectIdAsync(id, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetRunsAsync(id, request, ct));
    }

    [HttpPost("{id:int}/run")]
    public async Task<IActionResult> RunNow(int id, CancellationToken ct)
    {
        if (!await EnsurePolicyWriteAsync(id, ct)) return Forbid();
        var runId = await service.RunNowAsync(id, ct);
        return Ok(new { runId });
    }

    // --- Agent callbacks (AgentToken scheme; IDOR-guarded by the run's server in the service) ---
    [HttpPost("runs/{runId:int}/result")]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> ReportBackupResult(int runId, [FromBody] BackupExecuteResultDto result, CancellationToken ct)
    {
        if (!TryGetServerId(out var serverId)) return Forbid();
        return await service.ApplyBackupResultAsync(runId, serverId, result, ct) ? NoContent() : NotFound();
    }

    [HttpPost("runs/{runId:int}/restore-check-result")]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> ReportRestoreCheckResult(int runId, [FromBody] RestoreCheckResultDto result, CancellationToken ct)
    {
        if (!TryGetServerId(out var serverId)) return Forbid();
        return await service.ApplyRestoreCheckResultAsync(runId, serverId, result, ct) ? NoContent() : NotFound();
    }

    private async Task<bool> EnsurePolicyWriteAsync(int id, CancellationToken ct)
    {
        var projectId = await service.GetOwningProjectIdAsync(id, ct);
        return projectId is not null
            && await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Write, ct);
    }

    private bool TryGetServerId(out int serverId)
        => int.TryParse(User.FindFirstValue("ServerId"), out serverId);
}
