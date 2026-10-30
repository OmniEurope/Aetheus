// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PackageRegistry;

public interface IPackageRegistryRepository
{
    Task<RegistryPackage?> GetPackageAsync(PackageRegistryKind kind, string normalizedName, CancellationToken ct = default);

    /// <summary>True only when the package exists and an administrator opened it to anonymous reading
    /// (R-58); an unknown package reads false, so an anonymous caller cannot tell it from a private one.</summary>
    Task<bool> IsAnonymousDownloadAllowedAsync(
        PackageRegistryKind kind, string normalizedName, CancellationToken ct = default);
    Task<PackageRegistryRegistrationPackage?> GetRegistrationPackageAsync(
        PackageRegistryKind kind,
        string normalizedName,
        string? normalizedVersion = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>?> GetVersionNamesAsync(
        PackageRegistryKind kind, string normalizedName, CancellationToken ct = default);
    Task<RegistryPackageVersion?> GetVersionAsync(
        PackageRegistryKind kind, string normalizedName, string normalizedVersion, CancellationToken ct = default);
    Task<RegistryPackage?> GetPackageForUpdateAsync(PackageRegistryKind kind, string normalizedName, CancellationToken ct = default);

    /// <summary>The package with its versions and each version's files (multi-file formats such as
    /// Maven); tracked when <paramref name="forUpdate"/> is true.</summary>
    Task<RegistryPackage?> GetPackageWithFilesAsync(
        PackageRegistryKind kind, string normalizedName, bool forUpdate, CancellationToken ct = default);
    Task<RegistryPackage?> GetPackageByIdAsync(int id, CancellationToken ct = default);
    Task<RegistryPackage?> GetPackageByIdForUpdateAsync(int id, CancellationToken ct = default);
    Task<(List<PackageRegistrySearchPackage> Items, int Total)> SearchAsync(
        PackageRegistryKind kind, string? search, int skip, int take, bool includePrerelease, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, string>> GetVersionMetadataAsync(
        IReadOnlyCollection<int> versionIds, CancellationToken ct = default);
    Task<(List<PackageRegistryPackageDto> Items, int Total)> GetPagedAsync(
        PackageRegistryKind? kind, string? search, int page, int pageSize, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false, IReadOnlyList<GridFilter>? columnFilters = null);
    Task AddPackageAsync(RegistryPackage package, CancellationToken ct = default);
    Task<HashSet<string>> GetStoredFilePathsAsync(CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed record PackageRegistrySearchPackage(
    string Name,
    string? Description,
    string? DistTagsJson,
    IReadOnlyList<PackageRegistrySearchVersion> Versions);

public sealed record PackageRegistrySearchVersion(
    int Id,
    string Version,
    string NormalizedVersion,
    bool IsPrerelease,
    bool IsListed,
    DateTime CreatedAt);

public sealed record PackageRegistryRegistrationPackage(
    string Name,
    string? Description,
    IReadOnlyList<PackageRegistryRegistrationVersion> Versions);

public sealed record PackageRegistryRegistrationVersion(
    string Version,
    bool IsListed,
    DateTime CreatedAt,
    string Metadata,
    bool IsDeprecated = false);
