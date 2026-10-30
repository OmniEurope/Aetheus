// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;
using NuGet.Versioning;

namespace Aetheus.Back.Components.PackageRegistry;

internal sealed class NuGetRegistryService(
    IPackageRegistryRepository repository,
    IPackageRegistryStorage storage,
    NuGetPackageInspector inspector,
    PackageRegistryPublishGate publishGate,
    IAuditService audit,
    IAdminChangeNotifier notifier) : INuGetRegistryService
{
    public async Task PublishAsync(
        Func<Stream> openContent, string publishedBy, CancellationToken ct = default)
    {
        NuGetPackageMetadata metadata;
        await using (var inspectionStream = openContent())
            metadata = await inspector.InspectAsync(inspectionStream, ct).ConfigureAwait(false);

        using var lease = await publishGate.EnterAsync(
            PackageRegistryKind.NuGet, metadata.NormalizedName, ct).ConfigureAwait(false);
        var package = await repository.GetPackageForUpdateAsync(
            PackageRegistryKind.NuGet, metadata.NormalizedName, ct).ConfigureAwait(false);
        if (package?.Versions.Any(version => version.NormalizedVersion == metadata.NormalizedVersion) == true)
            throw new ConflictException($"NuGet package {metadata.Name} {metadata.Version} already exists.");

        StoredPackagePayload payload;
        await using (var content = openContent())
        {
            payload = await storage.SaveAsync(
                PackageRegistryKind.NuGet,
                metadata.NormalizedName,
                metadata.NormalizedVersion,
                ".nupkg",
                content,
                ct).ConfigureAwait(false);
        }

        package ??= new RegistryPackage
        {
            Kind = PackageRegistryKind.NuGet,
            Name = metadata.Name,
            NormalizedName = metadata.NormalizedName,
            Description = metadata.Description,
            AllowAnonymousDownload = PackageRegistryDefaults.IsPublicByDefault(PackageRegistryKind.NuGet, metadata.NormalizedName)
        };
        if (package.Id == 0)
            await repository.AddPackageAsync(package, ct).ConfigureAwait(false);
        else
            package.Description = metadata.Description;

        package.Versions.Add(new RegistryPackageVersion
        {
            Version = metadata.Version,
            NormalizedVersion = metadata.NormalizedVersion,
            FilePath = payload.RelativePath,
            ContentType = PackageRegistryDefaults.NuGetContentType,
            SizeBytes = payload.SizeBytes,
            Sha256 = payload.Sha256,
            Sha1 = payload.Sha1,
            Integrity = payload.Integrity,
            Metadata = JsonSerializer.Serialize(new StoredNuGetMetadata(
                metadata.Manifest, metadata.Authors, metadata.Tags, metadata.DependencyGroups)),
            IsPrerelease = metadata.IsPrerelease,
            PublishedBy = publishedBy
        });

        await PackageRegistryPublishFinalizer.SaveAndNotifyAsync(
            repository,
            storage,
            audit,
            notifier,
            payload,
            package,
            "NuGetPackage",
            $"{metadata.Name}@{metadata.Version}",
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>?> GetVersionsAsync(
        string packageId, CancellationToken ct = default)
    {
        NuGetPackageInspector.ValidatePackageId(packageId);
        var versions = await repository.GetVersionNamesAsync(
            PackageRegistryKind.NuGet, packageId.ToLowerInvariant(), ct).ConfigureAwait(false);
        if (versions is null)
            return null;
        return versions
            .Select(NuGetPackageInspector.ParseVersion)
            .OrderBy(version => version, VersionComparer.VersionRelease)
            .Select(version => version.ToNormalizedString().ToLowerInvariant())
            .ToList();
    }

    public async Task<RegistryPackageVersion?> GetVersionAsync(
        string packageId, string version, CancellationToken ct = default)
    {
        NuGetPackageInspector.ValidatePackageId(packageId);
        var normalizedVersion = NuGetPackageInspector.ParseVersion(version)
            .ToNormalizedString().ToLowerInvariant();
        return await repository.GetVersionAsync(
            PackageRegistryKind.NuGet, packageId.ToLowerInvariant(), normalizedVersion, ct)
            .ConfigureAwait(false);
    }

    public async Task<NuGetRegistration?> GetRegistrationAsync(
        string packageId, CancellationToken ct = default)
    {
        NuGetPackageInspector.ValidatePackageId(packageId);
        var package = await repository.GetRegistrationPackageAsync(
            PackageRegistryKind.NuGet, packageId.ToLowerInvariant(), ct: ct).ConfigureAwait(false);
        if (package is null)
            return null;

        var versions = package.Versions
            .OrderBy(version => NuGetPackageInspector.ParseVersion(version.Version), VersionComparer.VersionRelease)
            .Select(version =>
            {
                var stored = DeserializeMetadata(version.Metadata);
                return new NuGetRegistrationVersion(
                    version.Version,
                    version.IsListed,
                    version.CreatedAt,
                    stored.DependencyGroups,
                    version.IsDeprecated);
            })
            .ToList();
        var latestMetadata = package.Versions
            .OrderByDescending(version => NuGetPackageInspector.ParseVersion(version.Version), VersionComparer.VersionRelease)
            .Select(version => DeserializeMetadata(version.Metadata))
            .First();
        return new NuGetRegistration(
            package.Name,
            package.Description,
            latestMetadata.Authors,
            latestMetadata.Tags,
            versions);
    }

    public async Task<NuGetRegistration?> GetRegistrationVersionAsync(
        string packageId,
        string version,
        CancellationToken ct = default)
    {
        NuGetPackageInspector.ValidatePackageId(packageId);
        var normalizedVersion = NuGetPackageInspector.ParseVersion(version)
            .ToNormalizedString()
            .ToLowerInvariant();
        var package = await repository.GetRegistrationPackageAsync(
            PackageRegistryKind.NuGet,
            packageId.ToLowerInvariant(),
            normalizedVersion,
            ct).ConfigureAwait(false);
        if (package is null || package.Versions.Count == 0)
            return null;
        var selected = package.Versions.Single();
        var stored = DeserializeMetadata(selected.Metadata);
        return new NuGetRegistration(
            package.Name,
            package.Description,
            stored.Authors,
            stored.Tags,
            [
                new NuGetRegistrationVersion(
                    selected.Version,
                    selected.IsListed,
                    selected.CreatedAt,
                    stored.DependencyGroups,
                    selected.IsDeprecated)
            ]);
    }

    public async Task<NuGetSearchPage> SearchAsync(
        string? query, int skip, int take, bool includePrerelease, CancellationToken ct = default)
    {
        var (packages, total) = await repository.SearchAsync(
            PackageRegistryKind.NuGet,
            query,
            Math.Max(0, skip),
            Math.Clamp(take, 1, 200),
            includePrerelease,
            ct).ConfigureAwait(false);

        var selected = packages.Select(package =>
        {
            var versions = package.Versions
                .Where(version => version.IsListed && (includePrerelease || !version.IsPrerelease))
                .OrderByDescending(version => NuGetPackageInspector.ParseVersion(version.Version), VersionComparer.VersionRelease)
                .ToList();
            return (Package: package, Versions: versions, Latest: versions[0]);
        }).ToList();
        var metadata = await repository.GetVersionMetadataAsync(
            selected.Select(item => item.Latest.Id).ToList(), ct).ConfigureAwait(false);
        var results = selected.Select(item =>
        {
            var stored = DeserializeMetadata(metadata[item.Latest.Id]);
            return new NuGetSearchPackage(
                item.Package.Name,
                item.Latest.Version,
                item.Package.Description,
                stored.Authors,
                stored.Tags,
                item.Versions.Select(version => version.Version).ToList());
        }).ToList();
        return new NuGetSearchPage(total, results);
    }

    public async Task<bool> SetListedAsync(
        string packageId, string version, bool listed, string changedBy, CancellationToken ct = default)
    {
        NuGetPackageInspector.ValidatePackageId(packageId);
        var normalizedVersion = NuGetPackageInspector.ParseVersion(version)
            .ToNormalizedString().ToLowerInvariant();
        var package = await repository.GetPackageForUpdateAsync(
            PackageRegistryKind.NuGet, packageId.ToLowerInvariant(), ct).ConfigureAwait(false);
        var packageVersion = package?.Versions.SingleOrDefault(item => item.NormalizedVersion == normalizedVersion);
        if (packageVersion is null)
            return false;

        packageVersion.IsListed = listed;
        await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync(
            listed ? "Relisted" : "Unlisted",
            "NuGetPackage",
            package!.Id,
            $"{package.Name}@{packageVersion.Version} by {changedBy}",
            ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(
            AdminEntities.PackageRegistry, package.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return true;
    }

    public Stream? OpenContent(RegistryPackageVersion version) => storage.OpenRead(version.FilePath);

    public string ReadManifest(RegistryPackageVersion version)
        => DeserializeMetadata(version).Manifest;

    private static StoredNuGetMetadata DeserializeMetadata(RegistryPackageVersion version)
        => DeserializeMetadata(version.Metadata);

    private static StoredNuGetMetadata DeserializeMetadata(string metadata)
        => JsonSerializer.Deserialize<StoredNuGetMetadata>(metadata)
            ?? throw new InvalidOperationException("Stored NuGet metadata is invalid.");

    private sealed record StoredNuGetMetadata(
        string Manifest,
        string? Authors,
        string? Tags,
        IReadOnlyList<NuGetDependencyGroup> DependencyGroups);
}
