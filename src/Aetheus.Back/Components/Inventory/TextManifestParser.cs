// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Aetheus.Back.Components.Inventory;

/// <summary>Python, Rust, Ruby, Maven and Go. Each is a small text format, read without its package manager.</summary>
internal static partial class TextManifestParser
{
    /// <summary>The first Go release whose <c>go.mod</c> lists the whole module graph.</summary>
    private static readonly Version GoPrunedGraph = new(1, 17);

    /// <summary>Only <c>name==1.0.0</c> states a version; a range is counted, and a line of options is not a package.</summary>
    public static (List<InventoryEntry> Entries, int Unpinned) ParseRequirements(string text)
    {
        var entries = new List<InventoryEntry>();
        var unpinned = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split(" #")[0].Trim().TrimEnd('\\').Trim();
            if (line.Length == 0 || line[0] is '#' or '-') continue;
            var match = PinnedRequirement().Match(line);
            if (match.Success)
                entries.Add(new InventoryEntry(InventoryEcosystems.PyPI, match.Groups[1].Value, match.Groups[2].Value, IsDirect: true));
            else if (RequirementName().IsMatch(line)) unpinned++;
        }

        return (entries, unpinned);
    }

    /// <summary><c>poetry.lock</c>, <c>uv.lock</c> and <c>Cargo.lock</c> share one shape: <c>[[package]]</c> tables of <c>name</c> and <c>version</c>.</summary>
    public static List<InventoryEntry> ParseTomlLock(string text, string ecosystem)
    {
        var entries = new List<InventoryEntry>();
        string? name = null;
        string? version = null;
        var inPackage = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                Flush();
                inPackage = line == "[[package]]";
            }
            else if (inPackage && line.StartsWith("name = \"", StringComparison.Ordinal))
            {
                name = Quoted(line);
            }
            else if (inPackage && line.StartsWith("version = \"", StringComparison.Ordinal))
            {
                version = Quoted(line);
            }
        }

        Flush();
        return entries;

        void Flush()
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(version))
                entries.Add(new InventoryEntry(ecosystem, name, version, IsDirect: false));
            name = null;
            version = null;
        }
    }

    /// <summary>The <c>GEM</c> sections of <c>Gemfile.lock</c>: a spec is a line indented by four spaces, <c>name (version)</c>,
    /// and the dependencies of a spec (six spaces) are not packages. A platform suffix (<c>1.0.0-x86_64-linux</c>) is dropped;
    /// <c>GIT</c> and <c>PATH</c> sections carry no registry version and are left out. A line of another shape is ignored.
    /// A gem the <c>DEPENDENCIES</c> section names is direct.</summary>
    public static List<InventoryEntry> ParseGemfileLock(string text)
    {
        var entries = new List<InventoryEntry>();
        var direct = DirectGems(text);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inGem = false;
        var inSpecs = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                inGem = line.Trim() == "GEM";
                inSpecs = false;
            }
            else if (inGem && line.Trim() == "specs:")
            {
                inSpecs = true;
            }
            else if (inGem && inSpecs && GemSpec().Match(line) is { Success: true } match
                && seen.Add(match.Groups[1].Value + "@" + match.Groups[2].Value))
            {
                entries.Add(new InventoryEntry(InventoryEcosystems.RubyGems, match.Groups[1].Value, match.Groups[2].Value, direct.Contains(match.Groups[1].Value)));
            }
        }

        return entries;
    }

    private static HashSet<string> DirectGems(string text)
    {
        var direct = new HashSet<string>(StringComparer.Ordinal);
        var inDependencies = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) inDependencies = line.Trim() == "DEPENDENCIES";
            else if (inDependencies && GemDependency().Match(line) is { Success: true } match) direct.Add(match.Groups[1].Value);
        }

        return direct;
    }

    private static string Quoted(string line)
    {
        var start = line.IndexOf('"', StringComparison.Ordinal) + 1;
        var end = line.IndexOf('"', start);
        return end < 0 ? string.Empty : line[start..end];
    }

    /// <summary>The direct dependencies of one POM; an inherited or property-less version is counted.
    /// Transitive dependencies need Maven itself, so a POM is never complete.</summary>
    public static (List<InventoryEntry> Entries, int Unresolved) ParsePom(string xml)
    {
        var entries = new List<InventoryEntry>();
        var project = SafeXml.Load(xml).Root;
        if (project is null) return (entries, 0);
        var properties = Child(project, "properties")?.Elements()
            .GroupBy(item => item.Name.LocalName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolved = 0;
        foreach (var dependency in Child(project, "dependencies")?.Elements() ?? [])
        {
            var group = Child(dependency, "groupId")?.Value.Trim();
            var artifact = Child(dependency, "artifactId")?.Value.Trim();
            var version = ResolveProperty(Child(dependency, "version")?.Value.Trim(), properties);
            if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(artifact)) continue;
            if (string.IsNullOrEmpty(version)) unresolved++;
            else entries.Add(new InventoryEntry(InventoryEcosystems.Maven, $"{group}:{artifact}", version, IsDirect: true));
        }

        return (entries, unresolved);
    }

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    private static string? ResolveProperty(string? value, Dictionary<string, string> properties)
    {
        if (value is null || !value.StartsWith("${", StringComparison.Ordinal)) return value;
        return properties.GetValueOrDefault(value.Trim('$', '{', '}'))?.Trim();
    }

    /// <summary><c>go.mod</c> lists the whole graph since Go 1.17; before that it is only partial.</summary>
    public static (List<InventoryEntry> Entries, bool Complete) ParseGoMod(string text)
    {
        var entries = new List<InventoryEntry>();
        var inRequire = false;
        var complete = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("go ", StringComparison.Ordinal))
            {
                var version = line[3..].Trim();
                entries.Add(new InventoryEntry(InventoryEcosystems.Runtime, "go", version, IsDirect: true, ProjectInventoryItemKind.Runtime));
                complete = Version.TryParse(version.Split('-')[0], out var parsed) && parsed >= GoPrunedGraph;
            }
            else if (line == "require (")
            {
                inRequire = true;
            }
            else if (line == ")")
            {
                inRequire = false;
            }
            else if (inRequire)
            {
                AddGoRequirement(line, entries);
            }
            else if (line.StartsWith("require ", StringComparison.Ordinal))
            {
                AddGoRequirement(line["require ".Length..], entries);
            }
        }

        return (entries, complete);
    }

    private static void AddGoRequirement(string line, List<InventoryEntry> entries)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0].StartsWith("//", StringComparison.Ordinal)) return;
        entries.Add(new InventoryEntry(InventoryEcosystems.Go, parts[0], parts[1], !line.Contains("// indirect", StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"^ {2}([A-Za-z0-9][A-Za-z0-9._-]*)(?: \(.*\))?!?\s*$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex GemDependency();

    [GeneratedRegex(@"^ {4}([A-Za-z0-9][A-Za-z0-9._-]*) \(([0-9][A-Za-z0-9.]*)(?:-[A-Za-z0-9_.-]+)?\)\s*$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex GemSpec();

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9._-]*)(?:\[[^\]]*\])?\s*==\s*([^\s;,]+)\s*(?:;.*)?$")]
    private static partial Regex PinnedRequirement();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*(\[[^\]]*\])?\s*([<>=!~;].*)?$")]
    private static partial Regex RequirementName();
}
