// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Renames, in a copied definition, the identifiers the copy can recognise as the source project's
/// own, beyond the pipeline, library and vault names that <see cref="PipelineCopyAdapter"/> already
/// rewrites:
/// <list type="bullet">
/// <item>a value of a <c>variables:</c> mapping that is a bare identifier (letters, digits, hyphens,
/// underscores) equal to the source prefix or starting with <c>{prefix}-</c> or <c>{prefix}_</c>:
/// a compose project name, a container or stack name;</item>
/// <item>an environment name (<c>environment:</c>, or an item of <c>environments:</c>) under the same
/// rule with a hyphen. Environments are not copied, so the new project must create its own and the
/// readiness verdict names it.</item>
/// </list>
/// Everything else is left as written: host names, URLs, paths, image references, pinned runners,
/// shell text and any value that is not a bare identifier. A line is rewritten only where its YAML
/// structure identifies it, so this is a line scan of the formatting-preserving kind, not a free-text
/// replacement; lines inside a block scalar (a <c>shell: |</c> body) are never read. What it leaves
/// remains reported by <see cref="PipelineCopyAdapter"/> as still naming the source project.
/// </summary>
internal static partial class PipelineCopyIdentifierRenamer
{
    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:-[ \t]+)?(?:[^#\r\n]*:[ \t]*)?[|>][+\-0-9]*[ \t]*(?:#[^\r\n]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockScalarStart { get; }

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<key>variables|environments)[ \t]*:[ \t]*(?:#[^\r\n]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionStart { get; }

    [GeneratedRegex(@"^(?<lead>[ \t]+(?<key>[A-Za-z_][A-Za-z0-9_.\-]*)[ \t]*:[ \t]*)(?<quote>[""']?)(?<value>[a-z0-9][a-z0-9_\-]*)\k<quote>(?<tail>[ \t]*(?:#[^\r\n]*)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableLine { get; }

    [GeneratedRegex(@"^(?<lead>[ \t]*(?:-[ \t]+)?(?<key>environment)[ \t]*:[ \t]*)(?<quote>[""']?)(?<value>[a-z0-9][a-z0-9_\-]*)\k<quote>(?<tail>[ \t]*(?:#[^\r\n]*)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentLine { get; }

    [GeneratedRegex(@"^(?<lead>[ \t]+-[ \t]+)(?<quote>[""']?)(?<value>[a-z0-9][a-z0-9_\-]*)\k<quote>(?<tail>[ \t]*(?:#[^\r\n]*)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex ListItemLine { get; }

    /// <returns>The definition with the identifiers renamed, and one readable line per rename
    /// (<c>KEY: old -&gt; new</c>).</returns>
    internal static (string Yaml, List<string> Renamed) Rename(string yaml, string sourcePrefix, string prefix)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var renamed = new List<string>();
        if (string.IsNullOrEmpty(sourcePrefix) || string.IsNullOrEmpty(prefix)
            || string.Equals(sourcePrefix, prefix, StringComparison.Ordinal))
            return (yaml, renamed);

        var lines = yaml.Split('\n');
        var state = new ScanState();
        for (var index = 0; index < lines.Length; index++)
        {
            var terminator = lines[index].EndsWith('\r') ? "\r" : string.Empty;
            var line = terminator.Length == 0 ? lines[index] : lines[index][..^1];
            if (Skips(line, state)) continue;

            var rewritten = Rewrite(line, state.Section, sourcePrefix, prefix, renamed);
            if (rewritten is not null) lines[index] = rewritten + terminator;
        }
        return (string.Join('\n', lines), renamed);
    }

    /// <summary>Where the scan stands: inside a block scalar opened at some indentation, and in which
    /// <c>variables:</c> or <c>environments:</c> section.</summary>
    private sealed class ScanState
    {
        public int? BlockScalarParent { get; set; }
        public (string Key, int Indent)? Section { get; set; }
    }

    /// <summary>True when <paramref name="line"/> carries no value to rewrite: blank, a comment, inside a
    /// block scalar, or the start of a block scalar or a section (recorded in <paramref name="state"/>).</summary>
    private static bool Skips(string line, ScanState state)
    {
        if (line.Trim().Length == 0) return true;
        var indent = line.Length - line.TrimStart(' ', '\t').Length;
        if (state.BlockScalarParent is { } parent)
        {
            if (indent > parent) return true;
            state.BlockScalarParent = null;
        }
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#')) return true;
        if (state.Section is { } open && (indent < open.Indent || (indent == open.Indent && !trimmed.StartsWith('-'))))
            state.Section = null;

        if (BlockScalarStart.Match(line) is { Success: true } block)
        {
            state.BlockScalarParent = block.Groups["indent"].Length;
            return true;
        }
        if (SectionStart.Match(line) is { Success: true } start)
        {
            state.Section = (start.Groups["key"].Value, start.Groups["indent"].Length);
            return true;
        }
        return false;
    }

    private static string? Rewrite(
        string line, (string Key, int Indent)? section, string sourcePrefix, string prefix, List<string> renamed)
    {
        if (section is { Key: "variables" } && VariableLine.Match(line) is { Success: true } variable
            && Renamed(variable.Groups["value"].Value, sourcePrefix, prefix, "-_") is { } newVariable)
        {
            renamed.Add($"{variable.Groups["key"].Value}: {variable.Groups["value"].Value} -> {newVariable}");
            return Replace(variable, newVariable);
        }

        if (section is { Key: "environments" } && ListItemLine.Match(line) is { Success: true } item
            && Renamed(item.Groups["value"].Value, sourcePrefix, prefix, "-") is { } newItem)
        {
            renamed.Add($"environment: {item.Groups["value"].Value} -> {newItem}");
            return Replace(item, newItem);
        }

        if (EnvironmentLine.Match(line) is { Success: true } environment
            && Renamed(environment.Groups["value"].Value, sourcePrefix, prefix, "-") is { } newEnvironment)
        {
            renamed.Add($"environment: {environment.Groups["value"].Value} -> {newEnvironment}");
            return Replace(environment, newEnvironment);
        }

        return null;
    }

    /// <summary>The identifier with the source prefix replaced, when it is the prefix itself or the
    /// prefix followed by one of <paramref name="separators"/>; null otherwise.</summary>
    private static string? Renamed(string value, string sourcePrefix, string prefix, string separators)
    {
        if (string.Equals(value, sourcePrefix, StringComparison.Ordinal)) return prefix;
        return value.Length > sourcePrefix.Length + 1
               && value.StartsWith(sourcePrefix, StringComparison.Ordinal)
               && separators.Contains(value[sourcePrefix.Length])
            ? prefix + value[sourcePrefix.Length..]
            : null;
    }

    private static string Replace(Match match, string value)
    {
        var quote = match.Groups["quote"].Value;
        return $"{match.Groups["lead"].Value}{quote}{value}{quote}{match.Groups["tail"].Value}";
    }
}
