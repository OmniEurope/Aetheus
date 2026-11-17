// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>The version an update moves to, or the reason it moves to none. <see cref="IsMajor"/> is true when the watch's
/// recommendation was a major upgrade (the only fix crosses a major): such an update needs the user's review.</summary>
internal sealed record DependencyUpdateTarget(string? Version, string? Refusal, bool IsMajor = false)
{
    public static DependencyUpdateTarget To(string version, bool isMajor = false) => new(version, null, isMajor);
    public static DependencyUpdateTarget Refused(string reason, bool isMajor = false) => new(null, reason, isMajor);
}

/// <summary>
/// PLAN-007: which version an automatic update takes. What the watch recommends inside the current major
/// (<see cref="CveRecommendationKind.BumpDependency"/>), and, since the decision of 2026-11-10 (recette R-2, option B), a major
/// upgrade (<see cref="CveRecommendationKind.MajorUpgrade"/>) when no safe version exists inside the current major. The target is
/// checked again here rather than trusted: above the current version, not a pre-release unless the current version is one, and in the
/// same major for a bump, in a higher major for a major upgrade. A major update is flagged
/// (<see cref="DependencyUpdateTarget.IsMajor"/>), goes on its own branch and is left to the user's review. A pure function: nothing is read.
/// </summary>
internal static class DependencyUpdateSelection
{
    /// <summary>The ecosystems an update writes: NuGet, npm and (recette R-28) Packagist, under the same rules, and (recette R-24) the base
    /// images and the runtime versions, for a patch release only (<see cref="DependencyUpdateImageSelection"/>). The others come later
    /// (PLAN-007, remaining work).</summary>
    public static readonly IReadOnlySet<string> Ecosystems = new HashSet<string>(StringComparer.Ordinal)
    {
        "NuGet", "npm", "Packagist", DependencyUpdateImageSelection.Image, DependencyUpdateImageSelection.Runtime
    };

    public static DependencyUpdateTarget Choose(string ecosystem, string current, CveWatchRecommendationDto? recommendation)
    {
        if (DependencyUpdateImageSelection.Handles(ecosystem)) return DependencyUpdateImageSelection.Choose(ecosystem, current, recommendation);
        var major = recommendation?.Kind == CveRecommendationKind.MajorUpgrade;
        if (!Ecosystems.Contains(ecosystem))
            return DependencyUpdateTarget.Refused($"Automatic updates do not handle the {ecosystem} ecosystem yet.", major);
        if (recommendation is null)
            return DependencyUpdateTarget.Refused("The watch recommends nothing for this vulnerability.");
        if (recommendation.Kind is not (CveRecommendationKind.BumpDependency or CveRecommendationKind.MajorUpgrade)
            || string.IsNullOrWhiteSpace(recommendation.TargetVersion))
            return DependencyUpdateTarget.Refused("No version of the package fixes this vulnerability yet.", major);

        var target = recommendation.TargetVersion.Trim();
        if (VersionComparators.Compare(ecosystem, target, current) != VersionOrder.Greater)
            return DependencyUpdateTarget.Refused($"{target} is not above the current version {current}.", major);
        var sameMajor = PackageVersionOrdering.SameMajor(target, current);
        if (major && sameMajor)
            return DependencyUpdateTarget.Refused($"{target} is in the major of {current}: the watch calls it a major upgrade, so it is not trusted.", major);
        if (!major && !sameMajor)
            return DependencyUpdateTarget.Refused($"{target} is not in the major of {current}: only a recommendation that says so is followed across a major.");
        if (PackageVersionOrdering.IsPreRelease(ecosystem, target) && !PackageVersionOrdering.IsPreRelease(ecosystem, current))
            return DependencyUpdateTarget.Refused($"{target} is a pre-release and {current} is not.", major);
        return DependencyUpdateTarget.To(target, major);
    }
}
