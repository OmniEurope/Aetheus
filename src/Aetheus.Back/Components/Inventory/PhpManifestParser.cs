// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Inventory;

internal static partial class PhpManifestParser
{
    private const string PhpPlatform = "php";

    public static List<InventoryEntry> ParseLock(byte[] content)
    {
        using var document = JsonDocument.Parse(content, ManifestJson.Options);
        var root = document.RootElement;
        var entries = new List<InventoryEntry>();
        foreach (var section in new[] { "packages", "packages-dev" })
        {
            if (!root.TryGetProperty(section, out var packages) || packages.ValueKind != JsonValueKind.Array) continue;
            foreach (var package in packages.EnumerateArray())
            {
                var name = ManifestJson.Text(package, "name");
                var version = ManifestJson.Text(package, "version");
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(version))
                    entries.Add(new InventoryEntry(InventoryEcosystems.Packagist, name, version, IsDirect: false));
            }
        }

        return entries;
    }

    /// <summary>A constraint (<c>^8.2</c>) is not a version of a package: only an exact pin becomes an
    /// item; the others are counted. The PHP constraint is kept as the runtime the project states.</summary>
    public static (List<InventoryEntry> Entries, HashSet<string> Direct, int Unpinned) ParseProject(byte[] content)
    {
        using var document = JsonDocument.Parse(content, ManifestJson.Options);
        var entries = new List<InventoryEntry>();
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unpinned = 0;
        foreach (var section in new[] { "require", "require-dev" })
        {
            if (!document.RootElement.TryGetProperty(section, out var names) || names.ValueKind != JsonValueKind.Object) continue;
            foreach (var property in names.EnumerateObject())
            {
                var constraint = property.Value.GetString() ?? string.Empty;
                if (property.Name.Equals(PhpPlatform, StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(new InventoryEntry(InventoryEcosystems.Runtime, PhpPlatform, constraint, IsDirect: true, ProjectInventoryItemKind.Runtime));
                }
                else if (property.Name.Contains('/', StringComparison.Ordinal))
                {
                    direct.Add(property.Name);
                    if (ExactVersion().IsMatch(constraint))
                        entries.Add(new InventoryEntry(InventoryEcosystems.Packagist, property.Name, constraint, IsDirect: true));
                    else unpinned++;
                }
            }
        }

        return (entries, direct, unpinned);
    }

    [GeneratedRegex(@"^v?\d+(\.\d+)*$")]
    private static partial Regex ExactVersion();
}
