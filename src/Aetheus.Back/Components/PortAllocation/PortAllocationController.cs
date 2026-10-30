// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.PortAllocation;

/// <summary>Requires library write and server read permissions for allocation. The system reserves
/// ports under the project's identity, so server write permission is not required. The server must
/// belong to the organization of the library's project (RBAC audit, finding 4): reading a server of
/// another organization never lets a library reserve or probe its ports.</summary>
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
        if (!await ServerInLibraryOrganizationAsync(libraryId, request.ServerId, ct))
            return Forbid();
        return Ok(await service.CheckLibraryPortsAsync(libraryId, request.ServerId, ct));
    }

    /// <summary>
    /// Releases a port the library's project holds, offered after its <c>PORT_*</c> entry is deleted.
    /// It only ever releases the library's own project's reservation; the gate is library write and, on
    /// every server it releases on, the port reservation permission (R-11) of a server of the library's
    /// organization.
    /// </summary>
    [HttpDelete("{port:int}")]
    public async Task<ActionResult<int>> Release(int libraryId, int port, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, libraryId, Permission.Write, ct))
            return Forbid();
        foreach (var serverId in await service.GetReleaseServerIdsAsync(libraryId, ct))
        {
            if (!await authz.HasPermissionAsync(User, PermissionKeys.ServerPortsReserve, serverId, ct))
                return Forbid();
            if (!await ServerInLibraryOrganizationAsync(libraryId, serverId, ct))
                return Forbid();
        }
        return Ok(await service.ReleaseLibraryPortAsync(libraryId, port, ct));
    }

    [HttpPost("allocate")]
    public async Task<ActionResult<List<VariableEntryDto>>> Allocate(
        int libraryId, [FromBody] AllocateLibraryPortsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, libraryId, Permission.Write, ct))
            return Forbid();
        // R-11: reserving ports is its own permission (Contributor and above), never Server Read alone.
        if (!await authz.HasPermissionAsync(User, PermissionKeys.ServerPortsReserve, request.ServerId, ct))
            return Forbid();
        if (!await ServerInLibraryOrganizationAsync(libraryId, request.ServerId, ct))
            return Forbid();

        return Ok(await service.AllocateIntoLibraryAsync(libraryId, request.ServerId, request.Keys, ct));
    }

    private async Task<bool> ServerInLibraryOrganizationAsync(int libraryId, int serverId, CancellationToken ct)
    {
        var projectId = await service.GetLibraryProjectIdAsync(libraryId, ct);
        return await authz.BelongsToProjectOrganizationAsync(projectId, ResourceType.Server, [serverId], ct);
    }
}
