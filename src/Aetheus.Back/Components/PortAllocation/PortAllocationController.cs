// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.PortAllocation;

/// <summary>Requires library write and server read permissions for allocation. The system reserves
/// ports under the project's identity, so server write permission is not required.</summary>
[ApiController]
[Route("api/variable-libraries/{libraryId:int}/ports")]
[Authorize]
public class PortAllocationController(
    IPortAllocationService service,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet("servers")]
    public async Task<ActionResult<PortAllocationTargetsDto>> GetTargets(int libraryId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, libraryId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetTargetsAsync(libraryId, ct));
    }

    [HttpPost("check")]
    public async Task<ActionResult<PortCheckResultDto>> Check(
        int libraryId, [FromBody] AllocateLibraryPortsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, libraryId, Permission.Read, ct))
            return Forbid();
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, request.ServerId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.CheckLibraryPortsAsync(libraryId, request.ServerId, ct));
    }

    /// <summary>
    /// Releases a port the library's project holds, offered after its <c>PORT_*</c> entry is deleted.
    /// Library write is the gate: this only ever releases the library's own project's reservation.
    /// </summary>
    [HttpDelete("{port:int}")]
    public async Task<ActionResult<int>> Release(int libraryId, int port, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, libraryId, Permission.Write, ct))
            return Forbid();
        return Ok(await service.ReleaseLibraryPortAsync(libraryId, port, ct));
    }

    [HttpPost("allocate")]
    public async Task<ActionResult<List<VariableEntryDto>>> Allocate(
        int libraryId, [FromBody] AllocateLibraryPortsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, libraryId, Permission.Write, ct))
            return Forbid();
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, request.ServerId, Permission.Read, ct))
            return Forbid();

        return Ok(await service.AllocateIntoLibraryAsync(libraryId, request.ServerId, request.Keys, ct));
    }
}
