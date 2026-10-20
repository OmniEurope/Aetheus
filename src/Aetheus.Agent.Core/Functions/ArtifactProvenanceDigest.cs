// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The digests <c>generate-artifact-provenance.mjs</c> took, over a workspace root: per category, the
/// files below its paths (a symbolic link anywhere is refused, a missing path is empty), their count,
/// total size and a SHA-256 over the lines <c>&lt;relative path&gt;\0&lt;size&gt;\0&lt;sha256&gt;\n</c>
/// in ordinal path order. Files are hashed as streams, where the script read each one whole.
/// </summary>
internal sealed partial class ArtifactProvenanceDigest(string root)
{
    private const string AgentArchives = "agentArchives";
    private const string AgentManifest = ".pipeline-artifacts/agent-release/agent-release-manifest.json";
    private const double MaxSafeInteger = 9007199254740991d;

    [GeneratedRegex("^[0-9a-fA-F]{64}\\z")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\z")]
    private static partial Regex ArrayIndex();

    public string Root { get; } = root;

    /// <summary>The script's <c>target === root || target.startsWith(root + sep)</c>.</summary>
    public bool IsInside(string absolute) =>
        absolute == Root || absolute.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>A project's own category map: a non-empty object of workspace-relative path lists, each
    /// path inside the workspace. Keys come in <c>Object.entries</c> order (integer keys first).</summary>
    public async Task<IReadOnlyList<KeyValuePair<string, string[]>>> LoadCategoriesAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Root, path), cancellationToken).ConfigureAwait(false);
        if (JsonNode.Parse(text) is not JsonObject declared || declared.Count == 0)
            throw new InvalidDataException("The provenance category override declares no category.");
        var categories = new List<KeyValuePair<string, string[]>>();
        foreach (var (name, value) in EntriesInScriptOrder(declared))
            categories.Add(new(name, CategoryPaths(name, value)));
        return categories;
    }

    private string[] CategoryPaths(string name, JsonNode? value)
    {
        if (value is not JsonArray entries
            || entries.Any(entry => entry is not JsonValue item || !item.TryGetValue<string>(out var text) || text.Length == 0))
            throw new InvalidDataException($"Provenance category '{name}' must list workspace-relative paths.");
        var paths = entries.Select(entry => (string)entry!).ToArray();
        if (paths.Any(entry => !IsInside(Path.GetFullPath(Path.Combine(Root, entry)))))
            throw new InvalidDataException($"Provenance category '{name}' escapes the workspace.");
        return paths;
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> EntriesInScriptOrder(JsonObject declared) =>
        declared.Where(entry => IsArrayIndex(entry.Key))
            .OrderBy(entry => double.Parse(entry.Key, CultureInfo.InvariantCulture))
            .Concat(declared.Where(entry => !IsArrayIndex(entry.Key)));

    private static bool IsArrayIndex(string key) =>
        ArrayIndex().IsMatch(key) && double.Parse(key, CultureInfo.InvariantCulture) < 4294967295d;

    public async Task<JsonObject> SummarizeAllAsync(
        IReadOnlyList<KeyValuePair<string, string[]>> categories, CancellationToken cancellationToken)
    {
        var summaries = new JsonObject();
        foreach (var (name, paths) in categories)
        {
            var summary = await SummarizeAsync(paths, cancellationToken).ConfigureAwait(false);
            if (name == AgentArchives && DeclaredAgentArchives() is { } declared)
                summary["declaredArtifacts"] = declared;
            summaries[name] = summary;
        }
        return summaries;
    }

    public async Task<JsonObject> SummarizeAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var files = paths.SelectMany(FilesBelow).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            var length = stream.Length;
            totalBytes += length;
            var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
            digest.AppendData(Encoding.UTF8.GetBytes($"{relative}\0{length.ToString(CultureInfo.InvariantCulture)}\0{sha256}\n"));
        }
        return new JsonObject
        {
            ["fileCount"] = files.Count,
            ["totalBytes"] = totalBytes,
            ["sha256"] = Convert.ToHexStringLower(digest.GetHashAndReset())
        };
    }

    /// <summary>The script's <c>filesBelow</c>: missing is empty (a dangling link included, as
    /// <c>existsSync</c> follows links), a symbolic link is refused, a directory is walked.</summary>
    private IEnumerable<string> FilesBelow(string path)
    {
        var absolute = Path.GetFullPath(Path.Combine(Root, path));
        FileSystemInfo? entry = Directory.Exists(absolute) ? new DirectoryInfo(absolute)
            : File.Exists(absolute) ? new FileInfo(absolute) : null;
        if (entry is null)
            return [];
        if (entry.LinkTarget is not null)
            throw new InvalidDataException($"Artifact provenance refuses symlink: {path}");
        if (entry is FileInfo)
            return [absolute];
        return Directory.EnumerateFileSystemEntries(absolute)
            .Order(StringComparer.Ordinal)
            .SelectMany(child => FilesBelow(Path.GetRelativePath(Root, child)))
            .ToList();
    }

    /// <summary>The archives the agent release manifest declares, sorted by file name, or null when
    /// there is no manifest.</summary>
    private JsonArray? DeclaredAgentArchives()
    {
        var manifestPath = Path.Combine(Root, AgentManifest);
        if (!File.Exists(manifestPath))
            return null;
        var archives = JsonNode.Parse(File.ReadAllText(manifestPath))?["archives"];
        if (archives is not null and not JsonArray)
            throw new InvalidDataException("Agent release manifest contains an invalid archive contract.");
        var declared = (archives as JsonArray ?? []).Select(DeclaredArchive).ToList();
        declared.Sort((left, right) => ScriptCollation.Instance.Compare((string)left["fileName"]!, (string)right["fileName"]!));
        return [.. declared];
    }

    /// <summary>One declared archive: a file name, a size that is a safe non-negative integer
    /// (<c>Number.isSafeInteger</c>) and a 64-character SHA-256, else the script's refusal.</summary>
    private static JsonObject DeclaredArchive(JsonNode? archive)
    {
        if (archive is JsonObject entry
            && Text(entry["fileName"]) is { Length: > 0 } fileName
            && SafeSize(entry["sizeBytes"]) is { } size
            && Text(entry["sha256"]) is { } sha256 && Sha256Hex().IsMatch(sha256))
            return new JsonObject
            {
                ["fileName"] = fileName,
                ["sizeBytes"] = size,
                ["sha256"] = sha256.ToLowerInvariant()
            };
        throw new InvalidDataException("Agent release manifest contains an invalid archive contract.");
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? SafeSize(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var size)
        && size >= 0 && size <= MaxSafeInteger && Math.Floor(size) == size
            ? (long)size
            : null;
}
