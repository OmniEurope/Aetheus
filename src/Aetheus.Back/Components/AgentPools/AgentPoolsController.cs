// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.AgentPools;

/// <summary>
/// The agent pools, scoped to organizations (R-12): a caller reads and changes only the pools of the
/// organizations its roles cover (the authorization service resolves the pool's organization); a site Admin
/// reaches them all.
/// </summary>
[ApiController]
[Route("api/agent-pools")]
[Authorize]
public class AgentPoolsController(IAgentPoolService poolService, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<AgentPoolDto>>> GetPools(
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.AgentPool, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<AgentPoolDto>());
        return Ok(await poolService.GetPoolsAsync(request, accessibleIds, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AgentPoolDto>> GetPool(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.AgentPool, id, Permission.Read, ct))
            return Forbid();
        var pool = await poolService.GetPoolAsync(id, ct);
        if (pool is null) return NotFound();
        return Ok(pool);
    }

    [HttpPost]
    public async Task<ActionResult<AgentPoolDto>> CreatePool(
        [FromBody] CreateAgentPoolRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.AgentPool, null, Permission.Write, ct))
            return Forbid();
        // R-12, as for a project (ADR-059): the pool goes to an organization that lets the caller create pools.
        // Without a named organization, a non-admin's pool goes to the one organization that does; with several
        // the caller must name it. A site Admin's unnamed pool goes to the default organization.
        var organizationId = request.OrganizationId;
        if (organizationId is null)
        {
            if (!User.IsInRole(RoleNames.Admin))
            {
                var writable = await authz.GetOrganizationIdsWithPermissionAsync(User, ResourceType.AgentPool, Permission.Write, ct);
                if (writable.Count != 1)
                    return BadRequest("Choose the organization the agent pool belongs to.");
                organizationId = writable[0];
            }
        }
        else if (!await authz.CanCreateInOrganizationAsync(User, ResourceType.AgentPool, organizationId.Value, ct))
            return Forbid();
        if (!await authz.CanAccessAllAsync(User, ResourceType.Server, Permission.Write, request.ServerIds, ct))
            return Forbid();
        var pool = await poolService.CreatePoolAsync(request with { OrganizationId = organizationId }, ct);
        return CreatedAtAction(nameof(GetPool), new { id = pool.Id }, pool);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<AgentPoolDto>> UpdatePool(
        int id, [FromBody] UpdateAgentPoolRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.AgentPool, id, Permission.Write, ct))
            return Forbid();
        if (!await authz.CanAccessAllAsync(User, ResourceType.Server, Permission.Write, request.ServerIds, ct))
            return Forbid();
        var pool = await poolService.UpdatePoolAsync(id, request, ct);
        if (pool is null) return NotFound();
        return Ok(pool);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeletePool(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.AgentPool, id, Permission.Admin, ct))
            return Forbid();
        var deleted = await poolService.DeletePoolAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
