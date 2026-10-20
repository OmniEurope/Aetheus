// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Releases;

/// <summary>The release detail's creator, uses and build inputs.</summary>
[ApiController]
[Route("api/releases")]
[Authorize]
public class ReleaseProvenanceController(
    IReleaseProvenanceService service,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet("{id:int}/provenance")]
    public async Task<ActionResult<ReleaseProvenanceDto>> GetProvenance(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Release, id, Permission.Read, ct))
            return Forbid();
        var provenance = await service.GetProvenanceAsync(id, ct);
        return provenance is null ? NotFound() : Ok(provenance);
    }
}
