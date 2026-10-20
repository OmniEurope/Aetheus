// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Portsentry;

[ApiController]
[Route("api/servers/{serverId:int}/portsentry")]
[Authorize]
[ServiceFilter(typeof(ValidateServerExistsFilter))]
public class PortsentryController(IPortsentryService portsentryService, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PortsentryDataDto>> GetState(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var state = await portsentryService.GetStateAsync(serverId, ct);
        return Ok(state);
    }

    [HttpPost("action")]
    public async Task<IActionResult> ExecuteAction(int serverId, [FromBody] PortsentryActionRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await portsentryService.ExecuteActionAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("setup")]
    public async Task<IActionResult> Setup(int serverId, [FromBody] PortsentrySetupRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
            return Forbid();
        await portsentryService.SetupAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("logs")]
    public async Task<IActionResult> GetLogs(int serverId, [FromBody] PortsentryLogRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        await portsentryService.GetLogsAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("unblock")]
    public async Task<IActionResult> UnblockIp(int serverId, [FromBody] PortsentryUnblockRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await portsentryService.UnblockIpAsync(serverId, request, ct);
        return Ok();
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        await portsentryService.GetStatusAsync(serverId, ct);
        return Ok();
    }

    [HttpGet("whitelist")]
    public async Task<ActionResult<PaginatedResult<PortsentryWhitelistIpDto>>> GetWhitelist(
        int serverId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var list = await portsentryService.GetWhitelistAsync(serverId, request, ct);
        return Ok(list);
    }

    [HttpGet("blocked")]
    public async Task<ActionResult<PaginatedResult<PortsentryBlockedIpDto>>> GetBlockedIps(
        int serverId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        return Ok(await portsentryService.GetBlockedIpsAsync(serverId, request, ct));
    }

    // The protocols the blocked IPs grid's checkable Protocol filter offers.
    [HttpGet("filter-values")]
    public async Task<ActionResult<PortsentryFilterValuesDto>> GetFilterValues(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        return Ok(await portsentryService.GetFilterValuesAsync(serverId, ct));
    }

    [HttpPost("whitelist")]
    public async Task<ActionResult<PortsentryWhitelistIpDto>> AddWhitelistIp(int serverId, [FromBody] AddPortsentryWhitelistRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        var result = await portsentryService.AddWhitelistIpAsync(serverId, request, ct);
        return Ok(result);
    }

    [HttpDelete("whitelist/{id:int}")]
    public async Task<IActionResult> RemoveWhitelistIp(int serverId, int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        var removed = await portsentryService.RemoveWhitelistIpAsync(serverId, id, ct);
        return removed ? Ok() : NotFound();
    }
}
