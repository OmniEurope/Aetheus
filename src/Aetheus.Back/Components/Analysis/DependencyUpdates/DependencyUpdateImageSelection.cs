// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Analysis.DependencyBump;
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>
/// Recette R-24: which release an automatic update takes for a base image (<c>docker</c>) or a runtime version (<c>runtime</c>) the
/// watch matched. A patch only: the target is a full release (<c>10.0.15</c>, never a cycle, a pre-release or a variant) above the
/// current one, in the same major and minor series (<c>10.0.14</c> to <c>10.0.15</c>; <c>10.1.0</c> and <c>11.0.0</c> are refused, they
/// change the runtime and not only its fixes). For an image the target is the tag the file will carry: the version with the
/// image's own variant kept (<c>10.0.14-noble</c> moves to <c>10.0.15-noble</c>). A pure function: nothing is read.
/// </summary>
internal static partial class DependencyUpdateImageSelection
{
    public const string Image = "docker";
    public const string Runtime = "runtime";

    public static bool Handles(string ecosystem) => ecosystem is Image or Runtime;

    public static DependencyUpdateTarget Choose(string ecosystem, string current, CveWatchRecommendationDto? recommendation)
    {
        var what = ecosystem == Image ? "image" : "runtime";
        var expected = ecosystem == Image ? CveRecommendationKind.BumpBaseImage : CveRecommendationKind.BumpRuntime;
        if (recommendation is null)
            return DependencyUpdateTarget.Refused("The watch recommends nothing for this vulnerability.");
        if (recommendation.Kind != expected || string.IsNullOrWhiteSpace(recommendation.TargetVersion))
            return DependencyUpdateTarget.Refused($"No release of the {what} is known to fix this vulnerability yet.");

        var target = recommendation.TargetVersion.Trim();
        if (VersionComparators.Compare(ecosystem, target, current) != VersionOrder.Greater)
            return DependencyUpdateTarget.Refused($"{target} is not above the current version {current}.");
        if (Release().Match(target) is not { Success: true } wanted)
            return DependencyUpdateTarget.Refused($"{target} is not a full release number: only a patch release of the {what} is taken.");
        if (Series().Match(current) is not { Success: true } now)
            return DependencyUpdateTarget.Refused($"The current version {current} is not a release number, so its series cannot be told.");
        if (wanted.Groups[1].Value != now.Groups[1].Value || wanted.Groups[2].Value != now.Groups[2].Value)
        {
            return DependencyUpdateTarget.Refused(
                $"{target} is not in the {now.Groups[1].Value}.{now.Groups[2].Value} series of {current}: only a patch release of the {what} is taken, a new minor or major is a decision.");
        }

        return DependencyUpdateTarget.To(ecosystem == Image ? BaseImageBump.TagFor(current, target) : target);
    }

    /// <summary>The release a version names: three numbers and nothing else.</summary>
    [GeneratedRegex(@"^v?(\d{1,9})\.(\d{1,9})\.(\d{1,9})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Release();

    /// <summary>The series of the current version: its first two numbers, before any variant, label or digest.</summary>
    [GeneratedRegex(@"^v?(\d{1,9})\.(\d{1,9})\.\d{1,9}(?![0-9.])", RegexOptions.CultureInvariant)]
    private static partial Regex Series();
}
