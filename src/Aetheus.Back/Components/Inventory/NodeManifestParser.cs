// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Back.Components.Inventory;

/// <summary>
/// The JavaScript lock files. <c>pnpm-lock.yaml</c> and <c>yarn.lock</c> are read line by line: only
/// their keys matter, and a full YAML tree of a large lock file costs far more memory than this.
/// </summary>
internal static class NodeManifestParser
{
    private const string NodeModules = "node_modules/";

    public static List<InventoryEntry> ParseNpmLock(byte[] content)
    {
        using var document = JsonDocument.Parse(content, ManifestJson.Options);
        var root = document.RootElement;
        var entries = new List<InventoryEntry>();
        if (root.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
        {
            ReadPackages(packages, entries);
            return entries;
        }

        // lockfileVersion 1 nests the packages instead of listing them.
        if (root.TryGetProperty("dependencies", out var nested)) ReadNested(nested, entries);
        return entries;
    }

    private static void ReadPackages(JsonElement packages, List<InventoryEntry> entries)
    {
        foreach (var package in packages.EnumerateObject())
        {
            var marker = package.Name.LastIndexOf(NodeModules, StringComparison.Ordinal);
            // The root ("") and the workspace members are this repository, not its dependencies.
            if (marker < 0 || IsLink(package.Value)) continue;
            var version = ManifestJson.Text(package.Value, "version");
            if (string.IsNullOrEmpty(version)) continue;
            var name = ManifestJson.Text(package.Value, "name") ?? package.Name[(marker + NodeModules.Length)..];
            entries.Add(new InventoryEntry(InventoryEcosystems.Npm, name, version, IsDirect: false));
        }
    }

    private static bool IsLink(JsonElement package) =>
        package.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.True;

    private static void ReadNested(JsonElement dependencies, List<InventoryEntry> entries)
    {
        foreach (var package in dependencies.EnumerateObject())
        {
            var version = ManifestJson.Text(package.Value, "version");
            if (!string.IsNullOrEmpty(version))
                entries.Add(new InventoryEntry(InventoryEcosystems.Npm, package.Name, version, IsDirect: false));
            if (package.Value.TryGetProperty("dependencies", out var inner)) ReadNested(inner, entries);
        }
    }

    /// <summary>Keys of the <c>packages:</c> section: <c>name@1.0.0</c> (v9), <c>/name@1.0.0(peer@2)</c> (v6), <c>/name/1.0.0</c> (v5).</summary>
    public static List<InventoryEntry> ParsePnpmLock(string text)
    {
        var entries = new List<InventoryEntry>();
        var inPackages = false;
        foreach (var line in Lines(text))
        {
            if (line.Length > 0 && line[0] != ' ')
            {
                inPackages = line.TrimEnd() == "packages:";
                continue;
            }

            if (!inPackages || !line.StartsWith("  ", StringComparison.Ordinal) || line.Length < 3 || line[2] == ' ') continue;
            var key = line.Trim().TrimEnd(':').Trim('\'', '"').TrimStart('/');
            var peers = key.IndexOf('(', StringComparison.Ordinal);
            if (peers > 0) key = key[..peers];
            if (SplitPnpmKey(key) is { } package)
                entries.Add(new InventoryEntry(InventoryEcosystems.Npm, package.Name, package.Version, IsDirect: false));
        }

        return entries;
    }

    private static (string Name, string Version)? SplitPnpmKey(string key)
    {
        var separator = key.LastIndexOf('@');
        if (separator <= 0)
        {
            separator = key.LastIndexOf('/');
            if (separator <= 0) return null;
        }

        var version = key[(separator + 1)..];
        // A dependency fetched from a tarball or a Git host has a URL here, not a version.
        return version.Length == 0 || !char.IsDigit(version[0]) ? null : (key[..separator], version);
    }

    /// <summary>Yarn 1 (<c>version "1.0.0"</c>) and Yarn 2+ (<c>version: 1.0.0</c>), under a key such as <c>"@a/b@^1", "@a/b@^2":</c>.</summary>
    public static List<InventoryEntry> ParseYarnLock(string text)
    {
        var entries = new List<InventoryEntry>();
        string? name = null;
        foreach (var line in Lines(text))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] != ' ')
            {
                name = YarnEntryName(line);
                continue;
            }

            var trimmed = line.TrimStart();
            if (name is null || !trimmed.StartsWith("version", StringComparison.Ordinal)) continue;
            var version = trimmed["version".Length..].TrimStart(':', ' ').Trim().Trim('"');
            if (version.Length > 0) entries.Add(new InventoryEntry(InventoryEcosystems.Npm, name, version, IsDirect: false));
            name = null;
        }

        return entries;
    }

    private static string? YarnEntryName(string line)
    {
        var key = line.TrimEnd();
        if (!key.EndsWith(':') || key.StartsWith("__metadata", StringComparison.Ordinal)) return null;
        var first = key.TrimEnd(':').Trim('"').Split(", ")[0].Trim('"');
        if (first.Contains("@workspace:", StringComparison.Ordinal)) return null;
        var separator = first.IndexOf('@', 1);
        return separator <= 0 ? null : first[..separator];
    }

    /// <summary>The names this package asks for itself, and the runtime its <c>engines</c> states.</summary>
    public static (HashSet<string> Direct, InventoryEntry? Runtime) ParsePackageJson(byte[] content)
    {
        using var document = JsonDocument.Parse(content, ManifestJson.Options);
        var root = document.RootElement;
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies" })
            if (root.TryGetProperty(section, out var names) && names.ValueKind == JsonValueKind.Object)
                direct.UnionWith(names.EnumerateObject().Select(property => property.Name));

        var node = root.TryGetProperty("engines", out var engines) ? ManifestJson.Text(engines, "node") : null;
        return (direct, string.IsNullOrWhiteSpace(node) ? null : NodeRuntime(node));
    }

    public static InventoryEntry? ParseNvmrc(string text)
    {
        var version = text.Trim().TrimStart('v');
        return version.Length == 0 ? null : NodeRuntime(version);
    }

    private static InventoryEntry NodeRuntime(string version) =>
        new(InventoryEcosystems.Runtime, "nodejs", version.Trim(), IsDirect: true, ProjectInventoryItemKind.Runtime);

    private static IEnumerable<string> Lines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) yield return line;
    }
}
