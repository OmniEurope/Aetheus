// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Inventory;

/// <summary>
/// The base images of a Dockerfile. A stage built on an earlier stage names no image, and an image
/// that needs a build argument with no default stays unknown: counted, never guessed.
/// </summary>
internal static partial class DockerfileParser
{
    private const string LatestTag = "latest";

    public static (List<InventoryEntry> Entries, int Unresolved) Parse(string text)
    {
        var entries = new List<InventoryEntry>();
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (ArgumentLine().Match(line) is { Success: true } argument)
                arguments[argument.Groups[1].Value] = argument.Groups[2].Value.Trim().Trim('"', '\'');
            if (FromLine().Match(line) is not { Success: true } from) continue;

            var image = Substitution().Replace(from.Groups[1].Value, match => Argument(match, arguments));
            if (image.Contains('$', StringComparison.Ordinal)) unresolved++;
            else if (!image.Equals("scratch", StringComparison.OrdinalIgnoreCase) && !stages.Contains(image))
                entries.Add(ToEntry(image));
            if (from.Groups[2].Success) stages.Add(from.Groups[2].Value);
        }

        return (entries, unresolved);
    }

    private static string Argument(Match match, Dictionary<string, string> arguments)
    {
        var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
        if (arguments.TryGetValue(name, out var value)) return value;
        return match.Groups[2].Success ? match.Groups[2].Value : match.Value;
    }

    private static InventoryEntry ToEntry(string image)
    {
        var digestAt = image.IndexOf('@', StringComparison.Ordinal);
        var digest = digestAt < 0 ? null : image[(digestAt + 1)..];
        var reference = digestAt < 0 ? image : image[..digestAt];
        // A colon before the last slash is a registry port, not a tag.
        var tagAt = reference.LastIndexOf(':');
        var hasTag = tagAt > reference.LastIndexOf('/');
        var name = hasTag ? reference[..tagAt] : reference;
        var tag = hasTag ? reference[(tagAt + 1)..] : digest is null ? LatestTag : null;
        var version = tag is null ? digest! : digest is null ? tag : $"{tag}@{digest}";
        return new InventoryEntry(InventoryEcosystems.Docker, name, version, IsDirect: true, ProjectInventoryItemKind.BaseImage);
    }

    [GeneratedRegex(@"^FROM\s+(?:--\S+\s+)*(\S+)(?:\s+AS\s+(\S+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FromLine();

    [GeneratedRegex(@"^ARG\s+([A-Za-z_][A-Za-z0-9_]*)=(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArgumentLine();

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::?-([^}]*))?\}|\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Substitution();
}
