// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PackageRegistry;

public interface INuGetRegistryService
{
    Task PublishAsync(Func<Stream> openContent, string publishedBy, CancellationToken ct = default);
    Task<IReadOnlyList<string>?> GetVersionsAsync(string packageId, CancellationToken ct = default);
    Task<RegistryPackageVersion?> GetVersionAsync(string packageId, string version, CancellationToken ct = default);
    Task<NuGetRegistration?> GetRegistrationAsync(string packageId, CancellationToken ct = default);
    Task<NuGetRegistration?> GetRegistrationVersionAsync(
        string packageId,
        string version,
        CancellationToken ct = default);
    Task<NuGetSearchPage> SearchAsync(
        string? query, int skip, int take, bool includePrerelease, CancellationToken ct = default);
    Task<bool> SetListedAsync(
        string packageId, string version, bool listed, string changedBy, CancellationToken ct = default);
    Stream? OpenContent(RegistryPackageVersion version);
    string ReadManifest(RegistryPackageVersion version);
}

public sealed record NuGetSearchPage(int TotalHits, IReadOnlyList<NuGetSearchPackage> Packages);

public sealed record NuGetSearchPackage(
    string Id,
    string Version,
    string? Description,
    string? Authors,
    string? Tags,
    IReadOnlyList<string> Versions);

public sealed record NuGetRegistration(
    string Id,
    string? Description,
    string? Authors,
    string? Tags,
    IReadOnlyList<NuGetRegistrationVersion> Versions);

public sealed record NuGetRegistrationVersion(
    string Version,
    bool IsListed,
    DateTime PublishedAt,
    IReadOnlyList<NuGetDependencyGroup> DependencyGroups,
    bool IsDeprecated = false);

public sealed record NuGetDependencyGroup(
    string TargetFramework,
    IReadOnlyList<NuGetPackageDependency> Dependencies);

public sealed record NuGetPackageDependency(string Id, string Range);
