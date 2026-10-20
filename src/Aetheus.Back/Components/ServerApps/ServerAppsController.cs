// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.ServerApps;

[ApiController]
[Route("api/servers/{serverId:int}/apps")]
[Authorize]
public class ServerAppsController(IServerAppService service, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<ServerAppDto>>> GetApps(
        int serverId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();

        return Ok(await service.GetPageAsync(serverId, request, ct));
    }

    // The sources the grid's checkable Source filter offers.
    [HttpGet("filter-values")]
    public async Task<ActionResult<ServerAppFilterValuesDto>> GetFilterValues(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();

        return Ok(await service.GetFilterValuesAsync(serverId, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ServerAppDto>> GetApp(int serverId, int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();

        var app = await service.GetByIdAsync(serverId, id, ct);
        if (app is null) return NotFound();
        return Ok(app);
    }

    [HttpPost]
    public async Task<ActionResult<ServerAppDto>> CreateApp(int serverId, [FromBody] CreateServerAppRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();

        var app = await service.CreateAsync(serverId, request, ct);
        return CreatedAtAction(nameof(GetApp), new { serverId, id = app.Id }, app);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ServerAppDto>> UpdateApp(int serverId, int id, [FromBody] UpdateServerAppRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();

        var app = await service.UpdateAsync(serverId, id, request, ct);
        if (app is null) return NotFound();
        return Ok(app);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteApp(int serverId, int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
            return Forbid();

        var deleted = await service.DeleteAsync(serverId, id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
