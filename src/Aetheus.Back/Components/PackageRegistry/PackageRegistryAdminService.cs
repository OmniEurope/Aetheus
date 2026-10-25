// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.PackageRegistry;

internal sealed class PackageRegistryAdminService(
    IPackageRegistryRepository repository,
    IAuditService audit,
    IAdminChangeNotifier notifier) : IPackageRegistryAdminService
{
    public async Task<PaginatedResult<PackageRegistryPackageDto>> GetPackagesAsync(
        PackageRegistryKind? kind, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repository.GetPagedAsync(
            kind, request.Search, page, pageSize, ct, request.SortBy, request.SortDescending, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<PackageRegistryPackageDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PackageRegistryPackageDetailDto?> GetPackageAsync(
        int id, CancellationToken ct = default)
    {
        var package = await repository.GetPackageByIdAsync(id, ct).ConfigureAwait(false);
        return package is null ? null : new PackageRegistryPackageDetailDto
        {
            Id = package.Id,
            Kind = package.Kind,
            Name = package.Name,
            Description = package.Description,
            CreatedAt = package.CreatedAt,
            UpdatedAt = package.UpdatedAt,
            Versions = package.Versions
                .OrderByDescending(version => version.CreatedAt)
                .Select(version => new PackageRegistryVersionDto
                {
                    Id = version.Id,
                    Version = version.Version,
                    SizeBytes = version.SizeBytes,
                    Sha256 = version.Sha256,
                    IsListed = version.IsListed,
                    IsDeprecated = version.IsDeprecated,
                    PublishedBy = version.PublishedBy,
                    CreatedAt = version.CreatedAt
                })
                .ToList()
        };
    }

    public async Task<bool> SetListedAsync(
        int packageId, int versionId, bool listed, CancellationToken ct = default)
    {
        var package = await repository.GetPackageByIdForUpdateAsync(packageId, ct).ConfigureAwait(false);
        var version = package?.Versions.SingleOrDefault(item => item.Id == versionId);
        if (version is null)
            return false;

        version.IsListed = listed;
        await SaveAndAnnounceAsync(package!, version, listed ? "Relisted" : "Unlisted", ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SetDeprecatedAsync(
        int packageId, int versionId, bool deprecated, CancellationToken ct = default)
    {
        var package = await repository.GetPackageByIdForUpdateAsync(packageId, ct).ConfigureAwait(false);
        var version = package?.Versions.SingleOrDefault(item => item.Id == versionId);
        if (version is null)
            return false;

        version.IsDeprecated = deprecated;
        await SaveAndAnnounceAsync(package!, version, deprecated ? "Deprecated" : "Undeprecated", ct).ConfigureAwait(false);
        return true;
    }

    private async Task SaveAndAnnounceAsync(
        Aetheus.Back.Data.Entities.RegistryPackage package,
        Aetheus.Back.Data.Entities.RegistryPackageVersion version,
        string action,
        CancellationToken ct)
    {
        await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync(
            action,
            package.Kind switch
            {
                PackageRegistryKind.NuGet => "NuGetPackage",
                PackageRegistryKind.Maven => "MavenPackage",
                _ => "NpmPackage"
            },
            package.Id,
            $"{package.Name}@{version.Version}",
            ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(
            AdminEntities.PackageRegistry, package.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);
    }
}
