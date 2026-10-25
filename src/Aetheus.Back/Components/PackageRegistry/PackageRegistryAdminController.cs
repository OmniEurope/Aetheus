// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.PackageRegistry;

[ApiController]
[Route("api/package-registry")]
[Authorize(Roles = "Admin")]
public class PackageRegistryAdminController(IPackageRegistryAdminService registry) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<PackageRegistryPackageDto>>> GetPackages(
        [FromQuery] PackageRegistryKind? kind,
        [FromQuery] PaginationRequest request,
        CancellationToken ct)
        => Ok(await registry.GetPackagesAsync(kind, request, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PackageRegistryPackageDetailDto>> GetPackage(
        int id, CancellationToken ct)
    {
        var package = await registry.GetPackageAsync(id, ct);
        return package is null ? NotFound() : Ok(package);
    }

    [HttpPut("{packageId:int}/versions/{versionId:int}/deprecation")]
    public async Task<IActionResult> UpdateVersionDeprecation(
        int packageId,
        int versionId,
        [FromBody] UpdatePackageRegistryVersionDeprecationRequest request,
        CancellationToken ct)
    {
        var updated = await registry.SetDeprecatedAsync(
            packageId, versionId, request.IsDeprecated, ct);
        return updated ? NoContent() : NotFound();
    }

    [HttpPut("{packageId:int}/versions/{versionId:int}")]
    public async Task<IActionResult> UpdateVersion(
        int packageId,
        int versionId,
        [FromBody] UpdatePackageRegistryVersionRequest request,
        CancellationToken ct)
    {
        var updated = await registry.SetListedAsync(
            packageId, versionId, request.IsListed, ct);
        return updated ? NoContent() : NotFound();
    }
}
