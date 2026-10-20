// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>Reports project database usage and reclaims space from purged tables.</summary>
[ApiController]
[Route("api/data-storage")]
[Authorize]
[ProducesResponseType(StatusCodes.Status200OK)]
public class ProjectStorageController(
    IProjectStorageBudgetService budgets,
    IReclaimSpaceService reclaim,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>The usage of each project the caller can read, against its budget.</summary>
    [HttpGet("projects")]
    public async Task<ActionResult<ProjectStorageReportDto>> GetProjects(CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Project, Permission.Read, ct);
        return Ok(await budgets.GetReportAsync(accessibleIds, ct));
    }

    /// <summary>The purged tables, their size now and the « Récupérer la place » results.</summary>
    [HttpGet("reclaim")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ReclaimSpaceStatusDto>> GetReclaimStatus(CancellationToken ct) =>
        Ok(await reclaim.GetStatusAsync(ct));

    /// <summary>Starts the VACUUM FULL of one purged table; 409 while a pipeline runs.</summary>
    [HttpPost("reclaim")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReclaimSpaceResultDto>> Reclaim([FromBody] ReclaimSpaceRequest request, CancellationToken ct) =>
        Accepted(await reclaim.StartAsync(request.Table, User.Identity?.Name ?? "unknown", ct));
}
