// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Guards the convention that every HttpClient JSON read in
/// <c>src/Aetheus.Front/Components</c> (where PLAN-009 moved the former <c>Services</c> folder and
/// the api clients) passes <c>JsonOptions.Web</c>. Without
/// it the global UTC→Local DateTime converter (wired through that options
/// instance in <c>Components/Shared/JsonOptions.cs</c>) is bypassed, and timestamps
/// come back as UTC - silently. Bugs of this class don't fail tests, they
/// just make the UI show times in the wrong timezone, which is the worst
/// possible failure mode (no error, just quietly-wrong data).
///
/// We scan the source text directly: no Roslyn / Cecil dependency keeps the
/// test fast and self-contained, and the false-positive rate is zero because
/// the codebase style centres each call on the helper or literally includes
/// <c>JsonOptions.Web</c> in its argument list.
/// </summary>
public class JsonOptionsCoverageTests
{
    // Matches the start of a generic JSON-deserialize call. Balanced walks locate the
    // invocation because nested generic arguments make a `[^>]+` regex incomplete.
    private static readonly Regex CallStartRegex = new(
        @"\b(ReadFromJsonAsync|GetFromJsonAsync)\s*<",
        RegexOptions.Compiled);
    private static readonly Regex OptionsArgumentRegex = new(
        @"\bJsonOptions\s*\.\s*Web\b",
        RegexOptions.Compiled);

    [Fact]
    public void Every_JsonAsync_Call_In_Services_Passes_JsonOptionsWeb()
    {
        var front = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        var roots = new[] { Path.Combine(front, "Components") };

        var violations = new List<string>();
        var callsScanned = 0;

        foreach (var (_, file) in RepositoryScan.EnumerateUnion(roots, "*.cs"))
        {
            var raw = File.ReadAllText(file);
            var stripped = StripCommentsAndStrings(raw);

            foreach (var match in CallStartRegex.Matches(stripped).Cast<Match>())
            {
                callsScanned++;
                var openParen = FindCallOpenParen(stripped, match.Index + match.Length - 1);
                if (openParen < 0)
                {
                    var malformedLine = LineNumberAt(stripped, match.Index);
                    violations.Add($"{Path.GetFileName(file)}:{malformedLine} - malformed generic JSON call");
                    continue;
                }

                var closeParen = FindMatchingCloseParen(stripped, openParen);
                if (closeParen < 0) continue; // Unbalanced - the compiler is the right gate, not us.

                var args = stripped.Substring(openParen + 1, closeParen - openParen - 1);
                if (OptionsArgumentRegex.IsMatch(args)) continue;

                var line = LineNumberAt(stripped, match.Index);
                violations.Add($"{Path.GetFileName(file)}:{line} - {match.Groups[1].Value} call missing JsonOptions.Web");
            }
        }

        // Sanity: if the scan ever finds zero call sites, the path or regex is broken
        // and the test would silently pass forever. 50 is well below the real count
        // (current = 131) but high enough to fail loud on a broken scanner.
        Assert.True(callsScanned >= 50,
            $"Scanner found only {callsScanned} JSON call sites in {string.Join(", ", roots)} - likely a path or regex bug.");

        Assert.True(violations.Count == 0,
            "Calls to ReadFromJsonAsync/GetFromJsonAsync in Aetheus.Front/Components must pass "
            + "JsonOptions.Web to engage the global UTC→Local DateTime converter:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Scanner_Recognizes_Nested_Generic_Types_And_Exact_Options()
    {
        const string valid =
            "client.GetFromJsonAsync<PaginatedResult<List<ProjectDto>>>(path, JsonOptions.Web)";
        const string invalid =
            "client.GetFromJsonAsync<PaginatedResult<List<ProjectDto>>>(path, OtherJsonOptions.Web)";

        var match = Assert.Single(CallStartRegex.Matches(valid).Cast<Match>());
        var openParen = FindCallOpenParen(valid, match.Index + match.Length - 1);
        var closeParen = FindMatchingCloseParen(valid, openParen);

        Assert.Equal(valid.IndexOf('('), openParen);
        Assert.True(closeParen > openParen);
        Assert.Matches(OptionsArgumentRegex, valid[(openParen + 1)..closeParen]);
        Assert.DoesNotMatch(OptionsArgumentRegex, invalid[(openParen + 1)..invalid.LastIndexOf(')')]);
    }

    private static int FindCallOpenParen(string source, int openAngleIndex)
    {
        var depth = 1;
        for (var i = openAngleIndex + 1; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    if (depth != 0) break;

                    i++;
                    while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
                    return i < source.Length && source[i] == '(' ? i : -1;
            }
        }

        return -1;
    }

    private static int FindMatchingCloseParen(string source, int openIndex)
    {
        var depth = 1;
        for (var i = openIndex + 1; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '(': depth++; break;
                case ')':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }
        return -1;
    }

    private static int LineNumberAt(string source, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++) if (source[i] == '\n') line++;
        return line;
    }

    /// <summary>
    /// Removes comments and string literals so a <c>ReadFromJsonAsync</c> mention
    /// inside a doc comment (<c>ApiClient.Helpers.cs</c> has one) or a string
    /// doesn't false-positive the scan. Newlines are preserved so reported line
    /// numbers stay accurate against the original file.
    /// </summary>
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

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
