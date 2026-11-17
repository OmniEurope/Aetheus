// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch;

namespace Aetheus.Back.Components.Inventory;

/// <summary>A package URL split into its parts, decoded.</summary>
internal sealed record ParsedPurl(string Type, string? Namespace, string Name, string? Version, IReadOnlyDictionary<string, string> Qualifiers);

/// <summary>The one spelling of a package URL per ecosystem (package-url specification).</summary>
internal static class InventoryPurl
{
    private const string Scheme = "pkg:";

    public static string Build(string ecosystem, string packageName, string version)
    {
        var path = VulnerabilitySourceCatalog.Family(ecosystem) switch
        {
            InventoryEcosystems.NuGet => $"nuget/{Uri.EscapeDataString(packageName)}",
            InventoryEcosystems.Npm => $"npm/{Segments(packageName)}",
            InventoryEcosystems.Packagist => $"composer/{Segments(packageName)}",
            InventoryEcosystems.PyPI => $"pypi/{Uri.EscapeDataString(packageName)}",
            InventoryEcosystems.Maven => $"maven/{Segments(packageName.Replace(':', '/'))}",
            InventoryEcosystems.Go => $"golang/{Segments(packageName)}",
            InventoryEcosystems.Crates => $"cargo/{Uri.EscapeDataString(packageName)}",
            InventoryEcosystems.RubyGems => $"gem/{Uri.EscapeDataString(packageName)}",
            InventoryEcosystems.Debian => $"deb/debian/{Uri.EscapeDataString(packageName)}",
            InventoryEcosystems.Alpine => $"apk/alpine/{Uri.EscapeDataString(packageName)}",
            InventoryEcosystems.Ubuntu => $"deb/ubuntu/{Uri.EscapeDataString(packageName)}",
            InventoryEcosystems.Docker => $"docker/{Segments(packageName)}",
            _ => $"generic/{Segments(packageName)}"
        };
        return $"{Scheme}{path}@{Uri.EscapeDataString(version)}";
    }

    public static ParsedPurl? TryParse(string? purl)
    {
        if (string.IsNullOrWhiteSpace(purl) || !purl.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = purl[Scheme.Length..].TrimStart('/').Split('#')[0];
        var query = rest.IndexOf('?', StringComparison.Ordinal);
        var qualifiers = query < 0 ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ParseQualifiers(rest[(query + 1)..]);
        if (query >= 0) rest = rest[..query];

        var versionAt = rest.LastIndexOf('@');
        var version = versionAt > rest.IndexOf('/', StringComparison.Ordinal) ? Uri.UnescapeDataString(rest[(versionAt + 1)..]) : null;
        if (version is not null) rest = rest[..versionAt];
        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var space = parts.Length > 2 ? string.Join('/', parts[1..^1].Select(Uri.UnescapeDataString)) : null;
        return new ParsedPurl(parts[0].ToLowerInvariant(), space, Uri.UnescapeDataString(parts[^1]), version, qualifiers);
    }

    private static Dictionary<string, string> ParseQualifiers(string query)
    {
        var qualifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0) qualifiers[pair[..separator]] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return qualifiers;
    }

    private static string Segments(string value) => string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
}
