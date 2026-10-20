// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>The « Conservation des données » setting of a project.</summary>
[ApiController]
[Route("api/projects/{id:int}/data-retention")]
[Authorize]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public class ProjectDataRetentionController(
    IProjectDataRetentionService service,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ProjectDataRetentionDto>> Get(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, id, Permission.Read, ct))
            return Forbid();
        var retention = await service.GetAsync(id, ct);
        return retention is null ? NotFound() : Ok(retention);
    }

    [HttpPut]
    public async Task<ActionResult<ProjectDataRetentionDto>> Update(
        int id, [FromBody] UpdateProjectDataRetentionRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, id, Permission.Write, ct))
            return Forbid();
        var retention = await service.UpdateAsync(id, request, ct);
        return retention is null ? NotFound() : Ok(retention);
    }
}
