// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis;

/// <summary>The ranges one advisory states for one package: what <c>VulnerabilityAffectedPackage</c> holds.</summary>
public sealed record AdvisoryRanges(string? RangesJson, string? VersionsJson);

/// <summary>
/// The newest safe version above the current one, and the newest safe version in the current major. Null for
/// a side that has none.
/// </summary>
public readonly record struct SafeVersions(string? SameMajor, string? Newest);

/// <summary>
/// Picks, among the versions a registry publishes, the newest that no known advisory of the package affects.
/// "Safe" is proved, never assumed: a version an advisory cannot be evaluated on (unreadable ranges, a version
/// its comparator cannot read) is not safe. A pre-release is only offered to a project already on one.
/// </summary>
public static class CveSafeVersionSelector
{
    public static SafeVersions Select(
        string ecosystem, string current, IEnumerable<string> published, IReadOnlyCollection<AdvisoryRanges> advisories)
    {
        var allowPreRelease = PackageVersionOrdering.IsPreRelease(ecosystem, current);
        var safe = published
            .Distinct(StringComparer.Ordinal)
            .Where(version => allowPreRelease || !PackageVersionOrdering.IsPreRelease(ecosystem, version))
            .Where(version => VersionComparators.Compare(ecosystem, version, current) == VersionOrder.Greater)
            .Where(version => IsFreeOfEveryAdvisory(ecosystem, version, advisories))
            .ToList();
        return new SafeVersions(
            PackageVersionOrdering.Newest(ecosystem, safe.Where(version => PackageVersionOrdering.SameMajor(version, current))),
            PackageVersionOrdering.Newest(ecosystem, safe));
    }

    private static bool IsFreeOfEveryAdvisory(string ecosystem, string version, IReadOnlyCollection<AdvisoryRanges> advisories) =>
        advisories.All(advisory =>
            VulnerabilityVersionEvaluator.Evaluate(ecosystem, version, advisory.RangesJson, advisory.VersionsJson) is null);
}
