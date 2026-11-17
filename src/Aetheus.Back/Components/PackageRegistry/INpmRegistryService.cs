// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Nodes;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PackageRegistry;

public interface INpmRegistryService
{
    /// <param name="observabilityPackagesOnly">Set for a pipeline run's registry credential (<see cref="PackageRunTokens"/>):
    /// any package other than the observability ones is refused before anything is stored.</param>
    Task PublishAsync(
        string routePackageName, JsonDocument publishDocument, string publishedBy,
        CancellationToken ct = default, bool observabilityPackagesOnly = false);
    Task<JsonObject?> GetPackumentAsync(
        string packageName, string registryBaseUrl, CancellationToken ct = default);
    Task<JsonObject?> GetVersionAsync(
        string packageName, string selector, string registryBaseUrl, CancellationToken ct = default);
    Task<RegistryPackageVersion?> GetTarballAsync(
        string packageName, string fileName, CancellationToken ct = default);
    Task<NpmSearchPage> SearchAsync(
        string? query, int from, int size, CancellationToken ct = default);
    Stream? OpenContent(RegistryPackageVersion version);
}

public sealed record NpmSearchPage(int Total, IReadOnlyList<NpmSearchPackage> Packages);

public sealed record NpmSearchPackage(
    string Name,
    string Version,
    string? Description,
    DateTime PublishedAt);
