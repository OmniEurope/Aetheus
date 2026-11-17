// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-38: the variables the production setup decides for the production pipeline (the Compose file and the strategy), read from
/// and added to a definition that was created without them (the repository could not be read). A value the definition already
/// holds is never changed.
/// </summary>
internal static class PipelineProductionYaml
{
    public const string ComposeFileKey = "COMPOSE_FILE";
    public const string StrategyKey = "DEPLOY_STRATEGY";

    /// <summary>The value the <c>variables:</c> of the definition gives <paramref name="key"/>, without its quotes; null when absent.</summary>
    public static string? ReadVariable(string yaml, string key)
    {
        foreach (var line in Lines(yaml))
        {
            var text = line.Trim();
            if (!text.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            var value = text[(key.Length + 1)..].Trim().Trim('"', '\'');
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    /// <summary>The definition with <c>COMPOSE_FILE</c> and <c>DEPLOY_STRATEGY</c> added to its <c>variables:</c> when it holds none
    /// of that name (the block is added when there is none); unchanged when both are there or nothing is given.</summary>
    public static string WithMissingVariables(string yaml, string? composeFile, string? strategy)
    {
        var missing = new List<string>();
        if (!string.IsNullOrEmpty(composeFile) && ReadVariable(yaml, ComposeFileKey) is null)
            missing.Add($"  {ComposeFileKey}: \"{Escape(composeFile)}\"");
        if (!string.IsNullOrEmpty(strategy) && ReadVariable(yaml, StrategyKey) is null)
            missing.Add($"  {StrategyKey}: \"{Escape(strategy)}\"");
        if (missing.Count == 0) return yaml;

        var lines = Lines(yaml).ToList();
        var block = lines.FindIndex(line => line.TrimEnd() == "variables:");
        if (block < 0)
        {
            lines.Add("variables:");
            block = lines.Count - 1;
        }
        lines.InsertRange(block + 1, missing);
        return string.Join('\n', lines);
    }

    private static IEnumerable<string> Lines(string yaml) => yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
