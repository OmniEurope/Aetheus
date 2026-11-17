// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Servers;

/// <summary>Recette R-8 (option B of 2026-11-07): a server's daily reboot, scheduled by Aetheus. Reading needs the Read
/// right on the server; creating, changing and removing it need the Admin right (a reboot is a destructive action).</summary>
[ApiController]
[Route("api/servers/{serverId:int}/reboot-schedule")]
[Authorize]
[ServiceFilter(typeof(ValidateServerExistsFilter))]
public sealed class ServerRebootScheduleController(IServerRebootScheduleService service, IResourceAuthorizationService authz)
    : ControllerBase
{
    /// <summary>The schedule, or 204 when the server has none.</summary>
    [HttpGet]
    public async Task<ActionResult<ServerRebootScheduleDto>> Get(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var canManage = await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct);
        var schedule = await service.GetAsync(serverId, canManage, ct);
        return schedule is null ? NoContent() : Ok(schedule);
    }

    [HttpPut]
    public async Task<ActionResult<ServerRebootScheduleDto>> Save(
        int serverId, [FromBody] SaveServerRebootScheduleRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
            return Forbid();
        return Ok(await service.SaveAsync(serverId, request, User.Identity?.Name ?? "unknown", ct));
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
            return Forbid();
        await service.DeleteAsync(serverId, ct);
        return NoContent();
    }
}
