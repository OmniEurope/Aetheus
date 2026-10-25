// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The edits the exporter makes to a file on its way to the public repository, port of
/// <c>rewriteReferences</c>, <c>dropTestMethods</c>, <c>dropHookTasks</c> and <c>findForbidden</c> of
/// <c>scripts/export-public-distribution.mjs</c>. Text is handled as Latin-1 where the script
/// used <c>latin1</c>, so every byte survives and only the ASCII names change.
/// </summary>
internal static partial class PublicDistributionEdits
{
    private const int MaxHits = 40;

    [GeneratedRegex("^ {4}(\\[|///)")]
    private static partial Regex AttributeOrDocComment();

    [GeneratedRegex("=>|^ {4}\\{\\s*\\r?\\z")]
    private static partial Regex ArrowOrOpeningBrace();

    [GeneratedRegex("^ {4}\\{\\s*\\r?\\z")]
    private static partial Regex OpeningBrace();

    [GeneratedRegex("^ {4}\\}\\s*\\r?\\z")]
    private static partial Regex ClosingBrace();

    [GeneratedRegex(";\\s*\\r?\\z")]
    private static partial Regex StatementEnd();

    [GeneratedRegex("^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)+\\.(ps1|sh|mjs|cjs|js|csproj|slnx)\\z")]
    private static partial Regex FilePath();

    [GeneratedRegex("\\n( +)\"")]
    private static partial Regex IndentedKey();

    /// <summary>Rewrites the renamed paths where other files name them (a launcher called from a test, a
    /// compose file), longest first so <c>scripts/launch-core.ps1</c> is not half-rewritten by a shorter
    /// name.</summary>
    public static string RewriteReferences(string text, IReadOnlyList<KeyValuePair<string, string>> rename)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rename);
        var result = text;
        foreach (var (from, to) in rename.OrderByDescending(entry => entry.Key.Length))
        {
            var name = from[(from.LastIndexOf('/') + 1)..];
            var newName = to[(to.LastIndexOf('/') + 1)..];
            result = Replace(Replace(result, from, to), name, newName);
        }
        return result;
    }

    /// <summary><c>text.split(from).join(to)</c>: nothing to find in an empty name.</summary>
    private static string Replace(string text, string from, string to) =>
        from.Length == 0 ? text : text.Replace(from, to, StringComparison.Ordinal);

    /// <summary>Rule 1 of the manifest, mechanised: removes the named test methods (their attributes and
    /// doc comment included) from a C# test file whose members sit at a four-space indent. Line-based on
    /// purpose, not brace-counting: a test body often holds a regex or a string with unbalanced braces.
    /// Returns the names it could not find, so a renamed test stops the export instead of slipping out
    /// untouched.</summary>
    public static (string Text, IReadOnlyList<string> Missing) DropTestMethods(string text, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);
        var lines = text.Split('\n').ToList();
        var missing = new List<string>();
        foreach (var name in names)
        {
            if (!TryDropTest(lines, name))
                missing.Add(name);
        }
        return (string.Join('\n', lines), missing);
    }

    private static bool TryDropTest(List<string> lines, string name)
    {
        var signature = new Regex(@"^ {4}\S.*\b(void|Task)\s+" + Regex.Escape(name) + @"\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
        var index = lines.FindIndex(line => signature.IsMatch(line));
        if (index < 0)
            return false;
        var start = index;
        while (start > 0 && AttributeOrDocComment().IsMatch(lines[start - 1]))
            start--;
        var open = index;
        while (open < lines.Count && !ArrowOrOpeningBrace().IsMatch(lines[open]))
            open++;
        var blockBody = open < lines.Count && OpeningBrace().IsMatch(lines[open]);
        var end = FindEnd(lines, blockBody ? open + 1 : open, blockBody);
        if (end < 0)
            return false;
        if (end + 1 < lines.Count && JsValue.Trim(lines[end + 1]).Length == 0)
            end++;
        lines.RemoveRange(start, end - start + 1);
        return true;
    }

    private static int FindEnd(List<string> lines, int from, bool blockBody)
    {
        var terminator = blockBody ? ClosingBrace() : StatementEnd();
        for (var index = from; index < lines.Count; index++)
        {
            if (terminator.IsMatch(lines[index]))
                return index;
        }
        return -1;
    }

    /// <summary>Removes the named tasks from a Husky.Net <c>task-runner.json</c>: a hook that runs a script
    /// left out of the distribution would fail on every push there. Returns the names it could not find
    /// (a renamed task stops the export) and the remaining tasks whose arguments name a file the
    /// distribution does not carry, so the public hooks never run a check that cannot pass and never skip
    /// one silently. Null when the text is not a JSON object.</summary>
    public static HookEdit? DropHookTasks(string text, IReadOnlyList<string> names, IReadOnlySet<string> publishedPaths)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(publishedPaths);
        if (!PublicDistributionJson.TryParse(text.Length > 0 && text[0] == '﻿' ? text[1..] : text, out var parsed)
            || parsed is not JsObject runner)
            return null;
        var tasks = JsValue.Coalesce(runner["tasks"], new List<object?>()) as List<object?>;
        if (tasks is null)
            return null;

        var missing = names.Where(name => !tasks.Any(task => TaskName(task) == name)).ToList();
        var kept = tasks.Where(task => TaskName(task) is not { } name || !names.Contains(name, StringComparer.Ordinal)).ToList();
        runner.Set("tasks", kept);
        var dangling = kept
            .SelectMany(task => Arguments(task).Where(argument => FilePath().IsMatch(argument) && !publishedPaths.Contains(argument))
                .Select(argument => $"{TaskName(task) ?? "undefined"}: {argument}"))
            .ToList();
        return new HookEdit(Serialize(runner, text), missing, dangling);
    }

    /// <summary>The runner as the script re-wrote it: the indent of its first member, its line ending and
    /// its final line ending.</summary>
    private static string Serialize(JsObject runner, string original)
    {
        var eol = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = IndentedKey().Match(original) is { Success: true } match ? match.Groups[1].Length : 3;
        var json = JsJson.StringifyIndented(runner, indent)!.Replace("\n", eol, StringComparison.Ordinal);
        return original.EndsWith('\n') ? json + eol : json;
    }

    private static string? TaskName(object? task) => task is JsObject entry && entry["name"] is string name ? name : null;

    private static IEnumerable<string> Arguments(object? task) =>
        task is JsObject entry && entry["args"] is List<object?> arguments ? arguments.OfType<string>() : [];

    /// <summary>The literals that must never leave: <c>path: literal</c> for each hit.</summary>
    public static IReadOnlyList<string> FindForbidden(IEnumerable<KeyValuePair<string, string>> files, IReadOnlyList<string> forbidden)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(forbidden);
        return [.. files.SelectMany(file => forbidden
            .Where(literal => file.Value.Contains(literal, StringComparison.Ordinal))
            .Select(literal => $"{file.Key}: {literal}"))];
    }

    /// <summary>The first hits of a forbidden-literal report, as the script listed them.</summary>
    public static string Describe(IReadOnlyList<string> hits) =>
        $"{hits.Count} forbidden literal(s) in the distribution:\n  {string.Join("\n  ", hits.Take(MaxHits))}";
}

/// <summary>The result of <see cref="PublicDistributionEdits.DropHookTasks"/>: the runner text without the
/// dropped tasks, the names that matched no task and the remaining tasks naming a file the distribution
/// does not carry (<c>task: path</c>).</summary>
internal sealed record HookEdit(string Text, IReadOnlyList<string> Missing, IReadOnlyList<string> Dangling);
