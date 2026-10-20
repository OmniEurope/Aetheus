// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.ExternalRepos;

/// <summary>Attaches, inspects, synchronizes and detaches a project's mirror-backed Git source.
/// Every action is scoped to the project's organization.</summary>
[ApiController]
[Route("api/external-repos")]
[Authorize]
public class ExternalReposController(IExternalRepoService service, IResourceAuthorizationService authz) : ControllerBase
{
    // A project without an external repository is the normal case, not a missing resource: 204
    // keeps it out of the request error log, where every 404 is a warning.
    [HttpGet("project/{projectId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<ExternalRepoDto>> GetForProject(int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();

        var result = await service.GetForProjectAsync(projectId, ct);
        return result is null ? NoContent() : Ok(result);
    }

    [HttpPost("attach")]
    public async Task<ActionResult<ExternalRepoDto>> Attach([FromBody] AttachExternalRepoRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, request.ProjectId, Permission.Write, ct))
            return Forbid();

        var result = await service.AttachAsync(request, ct);
        return Ok(result);
    }

    [HttpPost("project/{projectId:int}/sync")]
    public async Task<ActionResult<ExternalRepoDto>> SyncNow(int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Write, ct))
            return Forbid();

        var result = await service.SyncNowAsync(projectId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("project/{projectId:int}")]
    public async Task<IActionResult> Detach(int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Write, ct))
            return Forbid();

        var ok = await service.DetachAsync(projectId, ct);
        return ok ? NoContent() : NotFound();
    }
}
