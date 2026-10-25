// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Which files of a repository are Compose files, and which of them is the production one (ADR-057). A
/// repository often keeps several (<c>dev-db.compose.yml</c>, <c>remote.compose.yml</c>): the wizard
/// pre-selects the one whose name or directory says it is for production, never one that says it is for
/// development, and lets the user choose another.
/// </summary>
internal static class ProductionComposeFiles
{
    private static readonly string[] ProductionWords = ["remote", "prod", "production", "deploy", "release"];
    private static readonly string[] DevelopmentWords = ["dev", "local", "test", "debug", "ci", "override", "e2e", "sample", "example"];

    public static bool IsComposeFile(string name) =>
        name.Contains("compose", StringComparison.OrdinalIgnoreCase)
        && (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    /// <summary>A path the deployment can pass to Compose: relative to the repository root, no traversal.</summary>
    public static bool IsSafePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 240 && !path.StartsWith('/') && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Split('/').Any(segment => segment is "" or "." or "..")
        && IsComposeFile(path[(path.LastIndexOf('/') + 1)..]);

    /// <summary>The Compose file that looks like production, else the only one, else none: a guess among several
    /// development-looking files would deploy the wrong one.</summary>
    public static string? Choose(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 1) return Score(paths[0]) < 0 ? null : paths[0];
        var best = paths.Select(path => (Path: path, Score: Score(path)))
            .OrderByDescending(item => item.Score).ThenBy(item => item.Path.Length).ThenBy(item => item.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        return best.Path is not null && best.Score > 0 ? best.Path : null;
    }

    private static int Score(string path)
    {
        var words = path.ToLowerInvariant().Split(['/', '.', '-', '_']).Where(word => word.Length > 0).ToList();
        if (words.Exists(DevelopmentWords.Contains)) return -3;
        return words.Exists(ProductionWords.Contains) ? 3 : 1;
    }
}
