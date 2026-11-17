// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PackageFeeds;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// The versions a public registry publishes for a package the watch matched, read through
/// <see cref="IPackageVersionResolver"/>. Only a package an advisory names is ever asked for, so a private
/// package of a project never reaches a registry. A listing is kept for an hour: a detail read is a person
/// looking at one vulnerability, and reading it twice should not ask the registry twice.
/// </summary>
internal sealed class CveWatchRegistryVersions(IPackageVersionResolver resolver, IMemoryCache cache)
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(1);

    /// <summary>The registry of an ecosystem the resolver can list, or null.</summary>
    public static PackageFeedType? FeedOf(string ecosystem) => ecosystem switch
    {
        "NuGet" => PackageFeedType.NuGet,
        "npm" => PackageFeedType.Npm,
        "Packagist" => PackageFeedType.Packagist,
        "PyPI" => PackageFeedType.PyPI,
        "Maven" => PackageFeedType.Maven,
        _ => null
    };

    /// <summary>The published versions, or null when the ecosystem has no registry here or the registry did not answer.</summary>
    public async Task<IReadOnlyList<string>?> ListAsync(string ecosystem, string packageName, CancellationToken ct)
    {
        if (FeedOf(ecosystem) is not { } feed) return null;
        var key = $"cve-watch-registry:{ecosystem}:{packageName}";
        if (cache.TryGetValue(key, out IReadOnlyList<string>? cached)) return cached;

        // An empty address is the public registry of the feed type.
        var result = await resolver.ListVersionsAsync(feed, string.Empty, packageName, ct).ConfigureAwait(false);
        if (result.Outcome != PackageResolveOutcome.Resolved) return null;
        IReadOnlyList<string> versions = [.. result.Versions.Select(version => version.Version)];
        cache.Set(key, versions, KeepFor);
        return versions;
    }
}
