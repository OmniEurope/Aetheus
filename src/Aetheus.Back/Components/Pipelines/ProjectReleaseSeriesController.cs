// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>The version series of a project: which release number each one hands out next, and the
/// administrator's way to fix the next version.</summary>
[ApiController]
[Route("api/projects/{id:int}/release-series")]
[Authorize]
public class ProjectReleaseSeriesController(
    IProjectReleaseSeriesService service,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ProjectReleaseSeriesDto>>> Get(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, id, Permission.Read, ct))
            return Forbid();
        var series = await service.GetAsync(id, ct);
        return series is null ? NotFound() : Ok(series);
    }

    /// <summary>Makes the requested number the next release number of a series; 409 when a release
    /// already took that version.</summary>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectReleaseSeriesDto>> SetNext(
        int id, [FromBody] SetNextReleaseNumberRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, id, Permission.Admin, ct))
            return Forbid();
        var series = await service.SetNextAsync(id, request, ct);
        return series is null ? NotFound() : Ok(series);
    }
}
