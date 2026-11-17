// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Inventory;

internal sealed record SbomComponent(string Name, string Version, string? PackageUrl, bool IsDirect);

internal sealed record SbomSnapshot(
    IReadOnlyList<int> ReportIds, string? CommitHash, string? BranchName, bool IsTruncated, IReadOnlyList<SbomComponent> Components);

/// <summary>
/// The components of an SBOM as inventory entries, by package URL. Debian and Alpine publish their
/// advisories on the source package: the <c>upstream</c> qualifier is what matches them, with its own
/// version when it states one. A component with no usable package URL is counted, never dropped silently.
/// </summary>
internal static partial class SbomInventoryMapper
{
    public static (List<InventoryEntry> Entries, List<InventoryGap> Gaps) Map(SbomSnapshot snapshot, int maxItems)
    {
        var entries = new List<InventoryEntry>();
        var gaps = new List<InventoryGap>();
        if (snapshot.IsTruncated || snapshot.Components.Count > maxItems) gaps.Add(new InventoryGap(InventoryGapCodes.SbomTruncated));
        foreach (var component in snapshot.Components.Take(maxItems))
        {
            var purl = InventoryPurl.TryParse(component.PackageUrl);
            var entry = purl is null ? null : ToEntry(purl, component);
            if (entry is null) gaps.Add(new InventoryGap(InventoryGapCodes.UnmappedComponent, purl?.Type));
            else entries.Add(entry);
        }

        return (entries, gaps);
    }

    private static InventoryEntry? ToEntry(ParsedPurl purl, SbomComponent component)
    {
        var version = purl.Version ?? component.Version;
        if (string.IsNullOrEmpty(version)) return null;
        var qualified = purl.Namespace is null ? purl.Name : $"{purl.Namespace}/{purl.Name}";
        return purl.Type switch
        {
            "nuget" => NuGet(purl.Name, version, component.IsDirect),
            "npm" => Package(InventoryEcosystems.Npm, qualified, version, component),
            "composer" => Package(InventoryEcosystems.Packagist, qualified, version, component),
            "pypi" => Package(InventoryEcosystems.PyPI, purl.Name, version, component),
            "maven" => Package(InventoryEcosystems.Maven, $"{purl.Namespace}:{purl.Name}", version, component),
            "golang" => Package(InventoryEcosystems.Go, qualified, version, component),
            "cargo" => Package(InventoryEcosystems.Crates, purl.Name, version, component),
            "gem" => Package(InventoryEcosystems.RubyGems, purl.Name, version, component),
            "deb" or "apk" => SystemPackage(purl, version),
            _ => null
        };
    }

    private static InventoryEntry Package(string ecosystem, string name, string version, SbomComponent component) =>
        new(ecosystem, name, version, component.IsDirect, SourcePath: InventoryOrigins.Sbom);

    /// <summary>The runtime packs a .NET image ships are how its runtime version reaches the watch.</summary>
    private static InventoryEntry NuGet(string name, string version, bool isDirect)
    {
        var isRuntime = InventoryOrigins.IsRuntimePack(name);
        return new InventoryEntry(
            InventoryEcosystems.NuGet, name, version, isDirect,
            isRuntime ? ProjectInventoryItemKind.Runtime : ProjectInventoryItemKind.Dependency, InventoryOrigins.Sbom);
    }

    private static InventoryEntry? SystemPackage(ParsedPurl purl, string version)
    {
        var name = purl.Name;
        if (purl.Qualifiers.TryGetValue("upstream", out var upstream) && upstream.Length > 0)
        {
            var separator = upstream.IndexOf('@', StringComparison.Ordinal);
            name = separator < 0 ? upstream : upstream[..separator];
            if (separator >= 0) version = upstream[(separator + 1)..];
        }

        var ecosystem = SystemEcosystem(purl);
        return ecosystem is null
            ? null
            : new InventoryEntry(ecosystem, name, version, IsDirect: false, ProjectInventoryItemKind.SystemPackage, InventoryOrigins.Sbom);
    }

    /// <summary>The ecosystem with its release, as OSV writes it: <c>Debian:12</c>, <c>Alpine:v3.19</c>, <c>Ubuntu:22.04</c>.</summary>
    private static string? SystemEcosystem(ParsedPurl purl)
    {
        var distro = purl.Qualifiers.GetValueOrDefault("distro") ?? string.Empty;
        var release = DistroRelease().Match(distro);
        return purl.Namespace?.ToLowerInvariant() switch
        {
            "debian" => release.Success ? $"{InventoryEcosystems.Debian}:{release.Groups[1].Value}" : InventoryEcosystems.Debian,
            "ubuntu" => release.Success ? $"{InventoryEcosystems.Ubuntu}:{release.Value}" : InventoryEcosystems.Ubuntu,
            "alpine" => release.Success ? $"{InventoryEcosystems.Alpine}:v{release.Value}" : InventoryEcosystems.Alpine,
            _ => null
        };
    }

    [GeneratedRegex(@"(\d+)(\.\d+)?")]
    private static partial Regex DistroRelease();
}
