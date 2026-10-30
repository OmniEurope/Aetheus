// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Environments;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EnvironmentsController(IEnvironmentService environmentService, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<EnvironmentDto>>> GetEnvironments(
        [FromQuery] int? projectId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Environment, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<EnvironmentDto>());
        return Ok(await environmentService.GetEnvironmentsAsync(projectId, request, accessibleIds, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<EnvironmentDto>> GetEnvironment(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Environment, id, Permission.Read, ct))
            return Forbid();
        var env = await environmentService.GetEnvironmentAsync(id, ct);
        if (env is null) return NotFound();
        return Ok(env);
    }

    [HttpPost]
    public async Task<ActionResult<EnvironmentDto>> CreateEnvironment(
        [FromBody] CreateEnvironmentRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Environment, null, Permission.Write, ct))
            return Forbid();
        // ADR-059: the new environment goes to a project of an organization that lets the caller write.
        if (!await authz.CanCreateInProjectAsync(User, ResourceType.Environment, request.ProjectId, ct))
            return Forbid();
        if (request.SourceEnvironmentId is { } sourceEnvironmentId
            && !await authz.HasPermissionAsync(
                User, ResourceType.Environment, sourceEnvironmentId, Permission.Read, ct))
            return Forbid();
        // M4: never a server of another organization than the environment's project.
        if (!await authz.BelongsToProjectOrganizationAsync(request.ProjectId, ResourceType.Server, request.ServerIds, ct))
            return Forbid();
        if (!await authz.CanAccessAllAsync(User, ResourceType.Server, Permission.Write, request.ServerIds, ct))
            return Forbid();
        // RBAC audit, finding 2: a copy brings the source's project-server links, which stay in the new
        // environment's organization.
        // A missing source is answered 404 by the service.
        var source = request.SourceEnvironmentId is null
            ? null
            : await environmentService.GetCopyReferencesAsync(request.SourceEnvironmentId.Value, ct);
        if (source is not null && !await CopyStaysInOrganizationAsync(source, request.ProjectId, copyServers: false, ct))
            return Forbid();
        var env = await environmentService.CreateEnvironmentAsync(request, ct);
        return CreatedAtAction(nameof(GetEnvironment), new { id = env.Id }, env);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<EnvironmentDto>> UpdateEnvironment(
        int id, [FromBody] UpdateEnvironmentRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Environment, id, Permission.Write, ct))
            return Forbid();
        // The project the environment ends up in must also let the caller write (no move out of reach).
        if (!await authz.CanCreateInProjectAsync(User, ResourceType.Environment, request.ProjectId, ct))
            return Forbid();
        // M4: never a server of another organization than the environment's project.
        if (!await authz.BelongsToProjectOrganizationAsync(request.ProjectId, ResourceType.Server, request.ServerIds, ct))
            return Forbid();
        if (!await authz.CanAccessAllAsync(User, ResourceType.Server, Permission.Write, request.ServerIds, ct))
            return Forbid();
        var env = await environmentService.UpdateEnvironmentAsync(id, request, ct);
        if (env is null) return NotFound();
        return Ok(env);
    }

    [HttpPost("{id:int}/duplicate")]
    public async Task<ActionResult<EnvironmentDto>> DuplicateEnvironment(
        int id, [FromBody] DuplicateEnvironmentRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Environment, id, Permission.Read, ct))
            return Forbid();
        if (!await authz.HasPermissionAsync(User, ResourceType.Environment, null, Permission.Write, ct))
            return Forbid();
        // Without a target the copy stays in the source's project, where the caller must be able to write.
        var canPlaceCopy = request.TargetProjectId is null
            ? await authz.HasPermissionAsync(User, ResourceType.Environment, id, Permission.Write, ct)
            : await authz.CanCreateInProjectAsync(User, ResourceType.Environment, request.TargetProjectId, ct);
        if (!canPlaceCopy)
            return Forbid();
        var references = await environmentService.GetCopyReferencesAsync(id, ct);
        if (references is null) return NotFound();
        // RBAC audit, finding 2: the copy keeps the source's servers and project-server links, so each must
        // belong to the copy's organization and the servers be ones the caller may write.
        if (!await CopyStaysInOrganizationAsync(references, request.TargetProjectId ?? references.ProjectId, copyServers: true, ct))
            return Forbid();
        var env = await environmentService.DuplicateEnvironmentAsync(id, request.TargetProjectId, ct);
        if (env is null) return NotFound();
        return CreatedAtAction(nameof(GetEnvironment), new { id = env.Id }, env);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteEnvironment(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Environment, id, Permission.Admin, ct))
            return Forbid();
        var deleted = await environmentService.DeleteEnvironmentAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:int}/project-servers/{projectServerId:int}")]
    public async Task<IActionResult> LinkProjectServer(int id, int projectServerId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Environment, id, Permission.Write, ct))
            return Forbid();
        var guard = await EnsureProjectServerAccessAsync(id, projectServerId, Permission.Write, ct);
        if (guard is not null) return guard;
        var result = await environmentService.LinkProjectServerAsync(id, projectServerId, ct);
        if (!result) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id:int}/project-servers/{projectServerId:int}")]
    public async Task<IActionResult> UnlinkProjectServer(int id, int projectServerId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Environment, id, Permission.Write, ct))
            return Forbid();
        var guard = await EnsureProjectServerAccessAsync(id, projectServerId, Permission.Read, ct);
        if (guard is not null) return guard;
        var result = await environmentService.UnlinkProjectServerAsync(id, projectServerId, ct);
        if (!result) return NotFound();
        return NoContent();
    }

    // Anti-IDOR: the Environment Write permission alone does not authorize referencing an
    // arbitrary ProjectServer. Verify the caller holds <projectPermission> on the ProjectServer's parent
    // project before (un)linking, and that it belongs to the environment's organization (RBAC audit,
    // finding 2): reading another organization's project does not let one of its servers into this one.
    // Linking makes the environment's runs target that project server, so it needs Write on its project
    // (multi-organization isolation audit); a Reader of that project may not enlist its servers.
    // Unlinking keeps Read on purpose: it only removes a reference from an environment the caller
    // already writes and grants nothing on the project server, and a caller who lost Write on the other
    // project must still be able to detach it from its own environment.
    // Returns null when access is granted, else the error result.
    private async Task<IActionResult?> EnsureProjectServerAccessAsync(
        int environmentId, int projectServerId, Permission projectPermission, CancellationToken ct)
    {
        var projectId = await environmentService.GetProjectServerProjectIdAsync(projectServerId, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, projectPermission, ct))
            return Forbid();
        var environment = await environmentService.GetEnvironmentAsync(environmentId, ct);
        if (environment is null) return NotFound();
        if (!await authz.BelongsToProjectOrganizationAsync(environment.ProjectId, ResourceType.Project, [projectId.Value], ct))
            return Forbid();
        return null;
    }

    /// <summary>True when what a copy carries over (<paramref name="references"/>) stays in the organization of
    /// <paramref name="targetProjectId"/>: the projects of its linked project servers and, for a duplicate,
    /// its servers, which the caller must also be able to write (as when it names servers itself).</summary>
    private async Task<bool> CopyStaysInOrganizationAsync(EnvironmentCopyReferences references, int? targetProjectId, bool copyServers, CancellationToken ct)
    {
        if (!await authz.BelongsToProjectOrganizationAsync(targetProjectId, ResourceType.Project, references.ProjectServerProjectIds, ct))
            return false;
        if (!copyServers) return true;
        return await authz.BelongsToProjectOrganizationAsync(targetProjectId, ResourceType.Server, references.ServerIds, ct)
            && await authz.CanAccessAllAsync(User, ResourceType.Server, Permission.Write, references.ServerIds, ct);
    }
}
