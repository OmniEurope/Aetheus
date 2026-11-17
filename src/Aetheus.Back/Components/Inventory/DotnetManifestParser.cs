// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Xml.Linq;

namespace Aetheus.Back.Components.Inventory;

/// <summary><c>packages.lock.json</c> states every package; the project files only the direct ones.</summary>
internal static class DotnetManifestParser
{
    private const string ProjectReference = "Project";
    private const string DirectReference = "Direct";

    /// <summary>One entry per framework and runtime target; a project reference is not a package.</summary>
    public static List<InventoryEntry> ParseLock(byte[] content)
    {
        using var document = JsonDocument.Parse(content, ManifestJson.Options);
        var entries = new List<InventoryEntry>();
        if (!document.RootElement.TryGetProperty("dependencies", out var targets)) return entries;
        foreach (var target in targets.EnumerateObject())
        {
            foreach (var package in target.Value.EnumerateObject())
            {
                var type = ManifestJson.Text(package.Value, "type");
                var version = ManifestJson.Text(package.Value, "resolved");
                if (type != ProjectReference && !string.IsNullOrEmpty(version))
                    entries.Add(new InventoryEntry(InventoryEcosystems.NuGet, package.Name, version, type == DirectReference));
            }
        }

        return entries;
    }

    public static Dictionary<string, string> ParseCentralVersions(string xml)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in SafeXml.Load(xml).Descendants().Where(element => element.Name.LocalName == "PackageVersion"))
        {
            var name = Attribute(item, "Include");
            var version = Attribute(item, "Version");
            if (name is not null && version is not null) versions[name] = version;
        }

        return versions;
    }

    /// <summary>A version that is a property or a floating range is not a version of a package: counted, not guessed.</summary>
    public static (List<InventoryEntry> Entries, int Unresolved) ParseProject(string xml, IReadOnlyDictionary<string, string> central)
    {
        var entries = new List<InventoryEntry>();
        var unresolved = 0;
        foreach (var reference in SafeXml.Load(xml).Descendants().Where(element => element.Name.LocalName == "PackageReference"))
        {
            var name = Attribute(reference, "Include") ?? Attribute(reference, "Update");
            if (name is null) continue;
            var version = Attribute(reference, "Version") ?? Attribute(reference, "VersionOverride");
            if (version is null) central.TryGetValue(name, out version);
            if (string.IsNullOrEmpty(version) || version.IndexOfAny(['$', '*', '[', '(', ',']) >= 0) unresolved++;
            else entries.Add(new InventoryEntry(InventoryEcosystems.NuGet, name, version, IsDirect: true));
        }

        return (entries, unresolved);
    }

    public static InventoryEntry? ParseGlobalJson(byte[] content)
    {
        using var document = JsonDocument.Parse(content, ManifestJson.Options);
        var version = document.RootElement.TryGetProperty("sdk", out var sdk) ? ManifestJson.Text(sdk, "version") : null;
        return string.IsNullOrEmpty(version)
            ? null
            : new InventoryEntry(InventoryEcosystems.Runtime, "dotnet", version, IsDirect: true, ProjectInventoryItemKind.Runtime);
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attribute(name)?.Value ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;
}

internal static class ManifestJson
{
    /// <summary>Hand-written manifests carry comments and trailing commas that their own tools accept.</summary>
    public static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
