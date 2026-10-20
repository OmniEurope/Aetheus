// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.PortRegistry;

[ApiController]
[Route("api/servers/{serverId:int}/ports")]
[Authorize]
public class PortRegistryController(
    IPortRegistryService service, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<PortReservationDto>>> GetReservations(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetServerReservationsAsync(serverId, ct));
    }

    [HttpPost("check")]
    public async Task<ActionResult<PortCheckResultDto>> CheckPorts(
        int serverId, [FromBody] PortCheckRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.CheckPortsAsync(serverId, request.Ports, contextProjectId: null, ct));
    }

    [HttpPost]
    public async Task<ActionResult<PortReservationDto>> CreateReservation(
        int serverId, [FromBody] CreatePortReservationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        return Ok(await service.AddManualReservationAsync(serverId, request, ct));
    }

    [HttpGet("range")]
    public async Task<ActionResult<PortRangeDto>> GetRange(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetPortRangeAsync(serverId, ct));
    }

    [HttpPut("range")]
    public async Task<ActionResult<PortRangeDto>> SetRange(
        int serverId, [FromBody] PortRangeDto range, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        return Ok(await service.SetPortRangeAsync(serverId, range, ct));
    }

    /// <summary>Allocates and reserves free ports under server write permission because
    /// reservations exclude other projects. Library-scoped allocation has its own narrower
    /// authorization rule.</summary>
    [HttpPost("allocate")]
    public async Task<ActionResult<List<int>>> Allocate(
        int serverId, [FromBody] AllocatePortsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();

        // Recorded under the project when one is named, so its own pipeline is not later refused the
        // ports this very allocation reserved for it.
        var ownerKey = request.ProjectId is { } projectId
            ? PortRegistryService.ProjectOwnerKey(projectId)
            : PortRegistryService.ManualOwnerKey(request.OwnerLabel);

        return Ok(await service.AllocateAsync(
            serverId, request.Count, ownerKey, request.OwnerLabel, request.ProjectId, ct));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> ReleaseReservation(int serverId, int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        return await service.ReleaseAsync(serverId, id, ct) ? NoContent() : NotFound();
    }
}
