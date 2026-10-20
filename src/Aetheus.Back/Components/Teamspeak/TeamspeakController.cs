// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Teamspeak;

[ApiController]
[Route("api/servers/{serverId:int}/teamspeak")]
[Authorize]
[ServiceFilter(typeof(ValidateServerExistsFilter))]
public class TeamspeakController(ITeamspeakService teamspeakService, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TeamspeakDataDto>> GetState(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var state = await teamspeakService.GetStateAsync(serverId, ct);
        return Ok(state);
    }

    [HttpGet("clients")]
    public async Task<ActionResult<PaginatedResult<TeamspeakClientDto>>> GetClients(
        int serverId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        return Ok(await teamspeakService.GetClientsAsync(serverId, request, ct));
    }

    // The platforms the clients grid's checkable Platform filter offers.
    [HttpGet("filter-values")]
    public async Task<ActionResult<TeamspeakFilterValuesDto>> GetFilterValues(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        return Ok(await teamspeakService.GetFilterValuesAsync(serverId, ct));
    }

    [HttpGet("channels")]
    public async Task<ActionResult<PaginatedResult<TeamspeakChannelDto>>> GetChannels(
        int serverId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        return Ok(await teamspeakService.GetChannelsAsync(serverId, request, ct));
    }

    [HttpPost("action")]
    public async Task<IActionResult> ExecuteAction(int serverId, [FromBody] TeamspeakActionRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await teamspeakService.ExecuteActionAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("setup")]
    public async Task<IActionResult> Setup(int serverId, [FromBody] TeamspeakSetupRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
            return Forbid();
        await teamspeakService.SetupAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("logs")]
    public async Task<IActionResult> GetLogs(int serverId, [FromBody] TeamspeakLogRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        await teamspeakService.GetLogsAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("kick")]
    public async Task<IActionResult> KickClient(int serverId, [FromBody] TeamspeakKickRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await teamspeakService.KickClientAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("ban")]
    public async Task<IActionResult> BanClient(int serverId, [FromBody] TeamspeakBanRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await teamspeakService.BanClientAsync(serverId, request, ct);
        return Ok();
    }

    // Item #9 tier-1
    [HttpPost("move-client")]
    public async Task<IActionResult> MoveClient(int serverId, [FromBody] TeamspeakMoveClientRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await teamspeakService.MoveClientAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("poke")]
    public async Task<IActionResult> PokeClient(int serverId, [FromBody] TeamspeakPokeClientRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await teamspeakService.PokeClientAsync(serverId, request, ct);
        return Ok();
    }

    [HttpGet("bans")]
    public async Task<ActionResult<PaginatedResult<TeamspeakBanDto>>> GetBans(
        int serverId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        return Ok(await teamspeakService.GetBansAsync(serverId, request, ct));
    }

    [HttpDelete("bans/{banId:int}")]
    public async Task<IActionResult> Unban(int serverId, int banId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await teamspeakService.UnbanAsync(serverId, banId, ct);
        return Ok();
    }

    [HttpPost("channels")]
    public async Task<IActionResult> CreateChannel(int serverId, [FromBody] TeamspeakCreateChannelRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await teamspeakService.CreateChannelAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPut("channels/{channelId:int}")]
    public async Task<IActionResult> EditChannel(int serverId, int channelId, [FromBody] TeamspeakEditChannelRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        request = request with { ChannelId = channelId };
        await teamspeakService.EditChannelAsync(serverId, request, ct);
        return Ok();
    }

    [HttpDelete("channels/{channelId:int}")]
    public async Task<IActionResult> DeleteChannel(int serverId, int channelId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await teamspeakService.DeleteChannelAsync(serverId, channelId, ct);
        return Ok();
    }

    [HttpPut("server")]
    public async Task<IActionResult> EditServer(int serverId, [FromBody] TeamspeakServerEditRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await teamspeakService.EditServerAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("message")]
    public async Task<IActionResult> SendGlobalMessage(int serverId, [FromBody] TeamspeakGlobalMessageRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await teamspeakService.SendGlobalMessageAsync(serverId, request, ct);
        return Ok();
    }

    // ===== Tier 2/3 - item #9 advanced ops =====
    // Read-only ones (clientinfo, list groups/tokens/complaints, serverinfo) gated by Read.
    // Mutating ones (graceful-restart, snapshot, group add/del, token add/del, complaint del)
    // gated by Write. Snapshot deploy adds a name-confirmation guard inside the service.

    [HttpPost("clientinfo")]
    public async Task<IActionResult> GetClientInfo(int serverId, [FromBody] TeamspeakClientInfoRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct)) return Forbid();
        await teamspeakService.GetClientInfoAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("graceful-restart")]
    public async Task<IActionResult> GracefulRestart(int serverId, [FromBody] TeamspeakGracefulRestartRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct)) return Forbid();
        await teamspeakService.GracefulRestartAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("snapshots")]
    public async Task<IActionResult> CreateSnapshot(int serverId, [FromBody] TeamspeakSnapshotCreateRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct)) return Forbid();
        await teamspeakService.CreateSnapshotAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("snapshots/deploy")]
    public async Task<IActionResult> DeploySnapshot(int serverId, [FromBody] TeamspeakSnapshotDeployRequest request, CancellationToken ct)
    {
        // Snapshot deploy is destructive enough to warrant Admin (rewrites channels/groups/perms).
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct)) return Forbid();
        await teamspeakService.DeploySnapshotAsync(serverId, request, ct);
        return Ok();
    }

    [HttpGet("server-groups")]
    public async Task<IActionResult> ListServerGroups(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct)) return Forbid();
        await teamspeakService.ListServerGroupsAsync(serverId, ct);
        return Ok();
    }

    [HttpPost("server-groups/add")]
    public async Task<IActionResult> AddClientToServerGroup(int serverId, [FromBody] TeamspeakServerGroupAddRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct)) return Forbid();
        await teamspeakService.AddClientToServerGroupAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("server-groups/remove")]
    public async Task<IActionResult> RemoveClientFromServerGroup(int serverId, [FromBody] TeamspeakServerGroupRemoveRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct)) return Forbid();
        await teamspeakService.RemoveClientFromServerGroupAsync(serverId, request, ct);
        return Ok();
    }

    [HttpGet("tokens")]
    public async Task<IActionResult> ListTokens(int serverId, CancellationToken ct)
    {
        // Tokens grant admin - listing is itself sensitive. Admin-only.
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct)) return Forbid();
        await teamspeakService.ListTokensAsync(serverId, ct);
        return Ok();
    }

    [HttpPost("tokens")]
    public async Task<IActionResult> CreateToken(int serverId, [FromBody] TeamspeakTokenCreateRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct)) return Forbid();
        await teamspeakService.CreateTokenAsync(serverId, request, ct);
        return Ok();
    }

    [HttpDelete("tokens")]
    public async Task<IActionResult> DeleteToken(int serverId, [FromBody] TeamspeakTokenDeleteRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct)) return Forbid();
        await teamspeakService.DeleteTokenAsync(serverId, request, ct);
        return Ok();
    }

    [HttpGet("server-info")]
    public async Task<IActionResult> GetServerInfo(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct)) return Forbid();
        await teamspeakService.GetServerInfoAsync(serverId, ct);
        return Ok();
    }

    [HttpGet("complaints")]
    public async Task<IActionResult> ListComplaints(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct)) return Forbid();
        await teamspeakService.ListComplaintsAsync(serverId, ct);
        return Ok();
    }

    [HttpDelete("complaints")]
    public async Task<IActionResult> DeleteComplaint(int serverId, [FromBody] TeamspeakComplaintDeleteRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct)) return Forbid();
        await teamspeakService.DeleteComplaintAsync(serverId, request, ct);
        return Ok();
    }
}
