// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.PackageFeeds;

/// <summary>Outcome of resolving a package's latest version against its upstream registry.</summary>
public enum PackageResolveOutcome
{
    Resolved,
    NotFound,
    Unsupported,
    RateLimited,
    Error
}

public readonly record struct PackageResolveResult(
    PackageResolveOutcome Outcome,
    string? LatestVersion,
    DateTime? PublishedAt,
    TimeSpan? RetryAfter = null);

/// <summary>One version a registry lists for a package, as the registry spells it.</summary>
public sealed record PublishedPackageVersion(string Version, DateTime? PublishedAt);

/// <summary>
/// Every version a registry publishes for a package. <see cref="Versions"/> is empty unless the outcome is
/// <see cref="PackageResolveOutcome.Resolved"/>: a failed or unreadable answer never yields a version.
/// </summary>
public readonly record struct PackageVersionListResult(
    PackageResolveOutcome Outcome,
    IReadOnlyList<PublishedPackageVersion> Versions,
    TimeSpan? RetryAfter = null);

public interface IPackageVersionResolver
{
    /// <summary>
    /// Queries the real upstream registry for the latest version of <paramref name="packageName"/>.
    /// Registry types without an implementation return <see cref="PackageResolveOutcome.Unsupported"/> -
    /// they are never given a fabricated version.
    /// </summary>
    Task<PackageResolveResult> ResolveLatestAsync(PackageFeedType feedType, string upstreamUrl, string packageName, CancellationToken ct = default);

    /// <summary>
    /// Queries the real upstream registry for every published version of <paramref name="packageName"/>, in the
    /// registry's own order and pre-releases included (a caller filters them). Registry types without an
    /// implementation return <see cref="PackageResolveOutcome.Unsupported"/>; an answer that cannot be read
    /// returns <see cref="PackageResolveOutcome.Error"/>. Nothing is ever guessed.
    /// </summary>
    Task<PackageVersionListResult> ListVersionsAsync(PackageFeedType feedType, string upstreamUrl, string packageName, CancellationToken ct = default);
}
