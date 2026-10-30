// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.ServiceConnections;

[ApiController]
[Route("api/service-connections")]
[Authorize]
public class ServiceConnectionsController(
    IServiceConnectionService service,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<ServiceConnectionDto>>> GetConnections(
        [FromQuery] int? projectId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.ServiceConnection, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<ServiceConnectionDto>());
        return Ok(await service.GetConnectionsAsync(projectId, request, accessibleIds, ct));
    }

    /// <summary>The project names the list's Project column filter offers.</summary>
    [HttpGet("filter-values")]
    public async Task<ActionResult<ServiceConnectionFilterValuesDto>> GetFilterValues(CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.ServiceConnection, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new ServiceConnectionFilterValuesDto());
        return Ok(await service.GetFilterValuesAsync(accessibleIds, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ServiceConnectionDetailDto>> GetConnection(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.ServiceConnection, id, Permission.Read, ct))
            return Forbid();

        var result = await service.GetConnectionAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<ServiceConnectionDto>> CreateConnection(
        [FromBody] CreateServiceConnectionRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.ServiceConnection, null, Permission.Write, ct))
            return Forbid();
        // ADR-059: the new resource goes to a project of an organization that lets the caller write.
        if (!await authz.CanCreateInProjectAsync(User, ResourceType.ServiceConnection, request.ProjectId, ct))
            return Forbid();

        var result = await service.CreateConnectionAsync(request, ct);
        return CreatedAtAction(nameof(GetConnection), new { id = result.Id }, result);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ServiceConnectionDto>> UpdateConnection(
        int id, [FromBody] UpdateServiceConnectionRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.ServiceConnection, id, Permission.Write, ct))
            return Forbid();
        // The project the resource ends up in must also let the caller write (no move out of reach).
        if (!await authz.CanCreateInProjectAsync(User, ResourceType.ServiceConnection, request.ProjectId, ct))
            return Forbid();

        var result = await service.UpdateConnectionAsync(id, request, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteConnection(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.ServiceConnection, id, Permission.Admin, ct))
            return Forbid();

        return await service.DeleteConnectionAsync(id, ct) ? NoContent() : NotFound();
    }

    [HttpPost("{id:int}/test")]
    public async Task<ActionResult<ServiceConnectionTestResultDto>> TestConnection(int id, CancellationToken ct)
    {
        // Requires Write, not Read: testing decrypts and USES the stored secret to reach an external host,
        // so a read-only viewer must not be able to exercise the credential.
        if (!await authz.HasPermissionAsync(User, ResourceType.ServiceConnection, id, Permission.Write, ct))
            return Forbid();

        var result = await service.TestConnectionAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }
}
