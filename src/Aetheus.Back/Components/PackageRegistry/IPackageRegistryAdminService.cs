// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.PackageRegistry;

public interface IPackageRegistryAdminService
{
    Task<PaginatedResult<PackageRegistryPackageDto>> GetPackagesAsync(
        PackageRegistryKind? kind, PaginationRequest request, CancellationToken ct = default);
    Task<PackageRegistryPackageDetailDto?> GetPackageAsync(int id, CancellationToken ct = default);
    Task<bool> SetListedAsync(int packageId, int versionId, bool listed, CancellationToken ct = default);
    Task<bool> SetDeprecatedAsync(int packageId, int versionId, bool deprecated, CancellationToken ct = default);
}
