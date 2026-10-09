// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Guards that every <c>ExecuteUpdateAsync</c> / <c>ExecuteDeleteAsync</c> call in
/// repository files is wrapped in an <c>if (db.Database.IsRelational())</c> check.
/// The InMemory EF Core provider does not support these bulk operations and throws
/// at runtime (500) - the guard ensures a fallback path exists for unit tests.
/// </summary>
public class ExecuteUpdateDeleteGuardTests
{
    private static readonly Regex BulkCallRegex = new(
        @"\b(ExecuteUpdateAsync|ExecuteDeleteAsync)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void Every_ExecuteUpdateDelete_Call_Must_Be_Guarded_By_IsRelational()
    {
        var componentsDir = Path.Combine(FindRepoRoot(), "src", "Aetheus.Back", "Components");
        Assert.True(Directory.Exists(componentsDir), $"Components dir not found: {componentsDir}");

        var violations = new List<string>();

        foreach (var file in RepositoryScan.Enumerate(componentsDir, "*.cs"))
        {
            var raw = File.ReadAllText(file);
            var stripped = StripCommentsAndStrings(raw);

            foreach (var match in BulkCallRegex.Matches(stripped).Cast<Match>())
            {
                // Walk backwards looking for IsRelational() in the containing block.
                // Generous lookback: server-delete cleans 16+ tables under a single guard.
                var start = Math.Max(0, match.Index - 3000);
                var context = stripped[start..match.Index];

                if (context.Contains("IsRelational()")) continue;

                var line = LineNumberAt(stripped, match.Index);
                violations.Add($"{Path.GetFileName(file)}:{line} - {match.Groups[1].Value} missing IsRelational() guard");
            }
        }

        Assert.True(violations.Count == 0,
            "ExecuteUpdateAsync/ExecuteDeleteAsync calls must be wrapped in "
            + "if (db.Database.IsRelational()) { } else { /* InMemory fallback */ }:\n  "
            + string.Join("\n  ", violations));
    }

    private static int LineNumberAt(string source, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++) if (source[i] == '\n') line++;
        return line;
    }

    private static string StripCommentsAndStrings(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var next = SkipCommentOrString(source, i, sb);
            if (next < 0) break;
            if (next > i)
            {
                i = next;
                continue;
            }
            sb.Append(source[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Recette R2-107 (one method per kind of span, under the grade-A cyclomatic threshold): the index
    /// after the comment (<c>/* */</c>, <c>//</c> and <c>///</c>) or string literal starting at
    /// <paramref name="i"/>, their line breaks kept in <paramref name="sb"/>; <paramref name="i"/> itself
    /// when none starts there; -1 for a block comment that never ends (the rest of the source is dropped).
    /// </summary>
    private static int SkipCommentOrString(string source, int i, StringBuilder sb)
    {
        if (string.CompareOrdinal(source, i, "/*", 0, 2) == 0)
        {
            var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
            if (end < 0) return -1;
            sb.Append('\n', source.AsSpan(i, end + 2 - i).Count('\n'));
            return end + 2;
        }
        if (string.CompareOrdinal(source, i, "//", 0, 2) == 0)
        {
            var lineEnd = source.IndexOf('\n', i);
            return lineEnd < 0 ? source.Length : lineEnd;
        }
        return source[i] == '"' ? SkipStringLiteral(source, i + 1, sb) : i;
    }

    /// <summary>Skips a string literal's content from <paramref name="i"/> (just after its quote), escapes included.</summary>
    private static int SkipStringLiteral(string source, int i, StringBuilder sb)
    {
        while (i < source.Length && source[i] != '"')
        {
            if (source[i] == '\\' && i + 1 < source.Length)
            {
                i += 2;
                continue;
            }
            if (source[i] == '\n') sb.Append('\n');
            i++;
        }
        return i < source.Length ? i + 1 : i;
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
