// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis;

/// <summary>What the calculator knows of one open match.</summary>
/// <param name="Ecosystem">As the advisory spells it (<c>npm</c>, <c>Debian:12</c>); it picks the order of versions.</param>
/// <param name="Kind">What the inventory item is: it picks the kind of action.</param>
/// <param name="SourcePath">The file that names the package, the one a bump changes.</param>
/// <param name="AdvisoryFixes">The fixes the advisory states above the current version (<see cref="CveAdvisoryFixes"/>).</param>
/// <param name="Registry">The safe versions the registry publishes, null when it was not (or could not be) read.</param>
/// <param name="EndOfLife">The version is out of support. Supplied by whoever knows the cycle; false is "not known to be".</param>
/// <param name="RuntimeFiles">The files of the project's inventory that state a runtime or SDK (<c>global.json</c>), empty when it holds none.</param>
/// <param name="ImageFiles">The files of the project's inventory that state a base image (a Dockerfile), empty when it holds none.</param>
public sealed record CveRecommendationInput(
    string Ecosystem,
    string CurrentVersion,
    ProjectInventoryItemKind Kind,
    string? SourcePath,
    IReadOnlyList<string> AdvisoryFixes,
    SafeVersions? Registry,
    bool EndOfLife,
    IReadOnlyList<string>? RuntimeFiles = null,
    IReadOnlyList<string>? ImageFiles = null);

/// <summary>
/// The recommendation of PLAN-004 "Action par CVE", computed with no language model and no network: a pure
/// function. A dependency is bumped inside its major when a safe version exists there, else upgraded across a
/// major; a runtime, a base image or an operating system package takes the nearest fix above its version. Without
/// any fix the answer is <see cref="CveRecommendationKind.EndOfLife"/> for an unsupported version, else
/// <see cref="CveRecommendationKind.NoFixYet"/>, which is only said where versions can be ordered: null otherwise.
/// </summary>
public static class CveRecommendationCalculator
{
    private readonly record struct Plan(CveRecommendationKind Kind, string Target, bool Verified);

    public static CveWatchRecommendationDto? Compute(CveRecommendationInput input)
    {
        var files = FilesOf(input);
        var plan = input.Kind == ProjectInventoryItemKind.Dependency ? ForDependency(input) : ForOtherItem(input);
        if (plan is { } found)
            return new CveWatchRecommendationDto { Kind = found.Kind, TargetVersion = found.Target, Files = files, VerifiedAgainstRegistry = found.Verified };
        if (input.EndOfLife)
            return new CveWatchRecommendationDto { Kind = CveRecommendationKind.EndOfLife, Files = files };
        return CanOrderVersions(input)
            ? new CveWatchRecommendationDto { Kind = CveRecommendationKind.NoFixYet, Files = files, VerifiedAgainstRegistry = input.Registry is not null }
            : null;
    }

    /// <summary>
    /// The files to change. A path that is an inventory origin (the SBOM, a server) is no file. A runtime or a runtime pack that comes from
    /// such an origin is raised through the SDK or the base image: the files of the project that state them, when the inventory knows
    /// any, else none. An operating system package never names a file.
    /// </summary>
    private static IReadOnlyList<string> FilesOf(CveRecommendationInput input)
    {
        if (InventoryOrigins.IsFile(input.SourcePath)) return [input.SourcePath!.Trim()];
        return input.Kind switch
        {
            ProjectInventoryItemKind.Runtime => [.. (input.RuntimeFiles ?? []).Concat(input.ImageFiles ?? []).Distinct(StringComparer.Ordinal)],
            ProjectInventoryItemKind.BaseImage => [.. (input.ImageFiles ?? []).Distinct(StringComparer.Ordinal)],
            _ => []
        };
    }

    private static Plan? ForDependency(CveRecommendationInput input)
    {
        if (input.Registry is { } registry)
        {
            if (registry.SameMajor is { } sameMajor) return new Plan(CveRecommendationKind.BumpDependency, sameMajor, true);
            return registry.Newest is { } newest ? new Plan(CveRecommendationKind.MajorUpgrade, newest, true) : null;
        }

        var inMajor = PackageVersionOrdering.Oldest(
            input.Ecosystem, input.AdvisoryFixes.Where(fix => PackageVersionOrdering.SameMajor(fix, input.CurrentVersion)));
        if (inMajor is not null) return new Plan(CveRecommendationKind.BumpDependency, inMajor, false);
        return PackageVersionOrdering.Oldest(input.Ecosystem, input.AdvisoryFixes) is { } anyMajor
            ? new Plan(CveRecommendationKind.MajorUpgrade, anyMajor, false)
            : null;
    }

    private static Plan? ForOtherItem(CveRecommendationInput input)
    {
        if (PackageVersionOrdering.Oldest(input.Ecosystem, input.AdvisoryFixes) is not { } nearest) return null;
        var kind = input.Kind switch
        {
            ProjectInventoryItemKind.Runtime => CveRecommendationKind.BumpRuntime,
            ProjectInventoryItemKind.BaseImage => CveRecommendationKind.BumpBaseImage,
            _ => CveRecommendationKind.OsUpdate
        };
        return new Plan(kind, nearest, false);
    }

    private static bool CanOrderVersions(CveRecommendationInput input) =>
        VersionComparators.Compare(input.Ecosystem, input.CurrentVersion, input.CurrentVersion) == VersionOrder.Equal;
}
