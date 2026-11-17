// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Servers;

/// <summary>Recette R-8: the agent's last check before it executes a reboot order. The order must still be the current
/// one of an enabled, unchanged schedule, within its deadline, with nothing else running on the server. The server is the
/// one of the agent's token, never a route value.</summary>
[ApiController]
[Route("api/servers/agent/reboot-orders")]
[Authorize(Policy = "AgentToken")]
public sealed class ServerRebootOrderController(IServerRebootScheduleService service) : ControllerBase
{
    [HttpPost("{taskId:int}/confirm")]
    public async Task<ActionResult<RebootOrderConfirmationDto>> Confirm(int taskId, CancellationToken ct)
    {
        var claim = User.FindFirst("ServerId")?.Value;
        if (claim is null || !int.TryParse(claim, out var serverId))
            return Forbid();
        return Ok(await service.ConfirmOrderAsync(serverId, taskId, ct));
    }
}
