// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Back.IntegrationTests.Architecture;

/// <summary>
/// Guards the convention that every HttpClient JSON read in the integration suite passes
/// <see cref="IntegrationJsonOptions.Default"/>. Those options carry a
/// <c>JsonStringEnumConverter</c>; without it, any response DTO with an enum property
/// (e.g. <c>ProjectStatus</c> on <c>ProjectDto</c>) deserializes through the default
/// number-only enum policy and blows up with <c>JsonException: ProjectStatus … could not
/// be converted</c> the moment the server serializes the enum as a string. That failure
/// class shipped four times before this guard existed; it is a deserialization-config bug,
/// not a behaviour bug, so the cheapest correct gate is a source scan.
///
/// We scan the source text directly (no Roslyn / Cecil dependency) so the test stays fast
/// and self-contained. The codebase style centres each call on the helper and literally
/// includes <c>IntegrationJsonOptions</c> in the argument list, so the false-positive rate
/// is zero. Mirrors the front guard <c>JsonOptionsCoverageTests</c>.
/// </summary>
public class IntegrationJsonOptionsCoverageTests
{
    // Matches the start of a generic JSON-deserialize call. The generic argument and invocation
    // are then found with balanced walks because both may contain nested generic calls.
    private static readonly Regex CallStartRegex = new(
        @"\b(ReadFromJsonAsync|GetFromJsonAsync)\s*<",
        RegexOptions.Compiled);
    private static readonly Regex OptionsArgumentRegex = new(
        @"\bIntegrationJsonOptions\s*\.\s*Default\b",
        RegexOptions.Compiled);

    [Fact]
    public void Every_JsonAsync_Call_Passes_IntegrationJsonOptionsDefault()
    {
        var projectDir = FindIntegrationProjectRoot();

        var violations = new List<string>();
        var callsScanned = 0;

        foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            // Skip generated output.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

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
                violations.Add($"{Path.GetFileName(file)}:{line} - {match.Groups[1].Value} call missing IntegrationJsonOptions.Default");
            }
        }

        // Sanity: a zero-call scan means the path or regex is broken and the test would
        // silently pass forever. The real count is comfortably above this floor.
        Assert.True(callsScanned >= 10,
            $"Scanner found only {callsScanned} JSON call sites in {projectDir} - likely a path or regex bug.");

        Assert.True(violations.Count == 0,
            "Calls to ReadFromJsonAsync/GetFromJsonAsync in Aetheus.Back.IntegrationTests must pass "
            + "IntegrationJsonOptions.Default to engage the string-enum converter (see ProjectStatus JsonException):\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Scanner_Recognizes_Nested_Generic_Json_Types()
    {
        const string source =
            "client.GetFromJsonAsync<PaginatedResult<List<ProjectDto>>>(path, IntegrationJsonOptions.Default)";

        var match = Assert.Single(CallStartRegex.Matches(source).Cast<Match>());
        var openParen = FindCallOpenParen(source, match.Index + match.Length - 1);

        Assert.Equal(source.IndexOf('('), openParen);
        var closeParen = FindMatchingCloseParen(source, openParen);
        Assert.True(closeParen > openParen);
        Assert.Matches(OptionsArgumentRegex, source[(openParen + 1)..closeParen]);
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
    /// Removes comments and string literals so a mention inside a doc comment or a string
    /// doesn't false-positive the scan. Newlines are preserved so reported line numbers stay
    /// accurate against the original file.
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

    /// <summary>
    /// Locates the integration-test project directory (the one holding <c>IntegrationJsonOptions.cs</c>)
    /// by walking up from the repo root, so the scan is independent of the build output layout.
    /// </summary>
    private static string FindIntegrationProjectRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(IntegrationJsonOptionsCoverageTests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aetheus.slnx")))
            {
                var projectDir = Path.Combine(dir.FullName, "tests", "Aetheus.Back.IntegrationTests");
                Assert.True(Directory.Exists(projectDir), $"Integration project dir not found: {projectDir}");
                return projectDir;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root (Aetheus.slnx).");
    }
}
