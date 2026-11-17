// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Back.Components.Inventory;

/// <summary>Every version of one package a lock file resolves, read by the parsers of the inventory, for whoever must know
/// what a package manager really chose rather than what was asked of it.</summary>
public static class LockFileVersions
{
    /// <summary>The versions of <paramref name="package"/> in the lock file, empty when it holds none. Null when the file is not one
    /// the inventory reads, or does not parse: the answer is then not known, which is not the same as "not there".</summary>
    public static IReadOnlyList<string>? Read(string fileName, byte[] content, string package)
    {
        var entries = Parse(fileName, content);
        return entries?.Where(entry => string.Equals(entry.Name, package, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Version).Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<InventoryEntry>? Parse(string fileName, byte[] content)
    {
        try
        {
            return fileName.ToLowerInvariant() switch
            {
                "packages.lock.json" => DotnetManifestParser.ParseLock(content),
                "package-lock.json" or "npm-shrinkwrap.json" => NodeManifestParser.ParseNpmLock(content),
                "composer.lock" => PhpManifestParser.ParseLock(content),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
