// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.DependencyUpdates;
namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>PLAN-007: the automatic dependency updates of one project. Read with the project's read right, the setting changed with
/// its write right; a project the caller may not read is a 404.</summary>
[ApiController]
[Route("api/projects/{projectId:int}/dependency-updates")]
[Authorize]
public sealed class DependencyUpdatesController(IDependencyUpdateOverviewService updates, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DependencyUpdatesOverviewDto>> Get(int projectId, CancellationToken ct = default)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct)) return NotFound();
        var overview = await updates.GetAsync(projectId, ct);
        return overview is null ? NotFound() : Ok(overview);
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SetEnabled(int projectId, [FromBody] UpdateDependencyUpdateSettingsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct)) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Write, ct)) return Forbid();
        return await updates.SetEnabledAsync(projectId, request.Enabled, ct) ? NoContent() : NotFound();
    }
}
