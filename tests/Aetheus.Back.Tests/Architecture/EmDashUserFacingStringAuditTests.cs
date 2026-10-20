// SPDX-License-Identifier: EUPL-1.2
using System.IO;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// S-TECH-EMDG: the project forbids the em-dash character (U+2014, "—") in user-facing text. The
/// user-facing string literals were swept once; this guard keeps them swept. It fails the build if a
/// double-quoted string literal in production C#/Razor or a localized RESX value contains a raw
/// em-dash - a task label, toast, confirm message, or badge text that would surface the forbidden glyph
/// to a human.
///
/// Deliberately narrow to avoid false positives: it inspects only <b>double-quoted string literals</b>,
/// skipping line comments (<c>//</c>, <c>///</c>), block comments (<c>/* */</c>) and Razor comments
/// (<c>@* *@</c>), where an em-dash is a code annotation, not output. It does NOT police Razor markup
/// prose (not a string literal) - that is handled by review, not this guard.
/// </summary>
public sealed class EmDashUserFacingStringAuditTests
{
    private const char EmDash = '—';

    [Fact]
    public void No_user_facing_string_literal_contains_an_em_dash()
    {
        var repoRoot = FindRepoRoot();
        var roots = new[]
        {
            (Path.Combine(repoRoot, "src", "Aetheus.Front"), new[] { "*.razor", "*.cs", "*.resx" }),
            (Path.Combine(repoRoot, "src", "Aetheus.Back", "Components"), new[] { "*.cs" }),
        };

        var violations = new List<string>();
        foreach (var (dir, patterns) in roots)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pattern in patterns)
                foreach (var file in RepositoryScan.Enumerate(dir, pattern))
                {
                    if (IsGenerated(file)) continue;
                    var inBlockComment = false;
                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                        if ((file.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)
                                && lines[i].Contains(EmDash))
                            || LineHasEmDashInStringLiteral(lines[i], ref inBlockComment))
                            violations.Add($"  - {Path.GetRelativePath(repoRoot, file)}:{i + 1}");
                }
        }

        Assert.True(violations.Count == 0,
            "User-facing string literals must not contain the em-dash — (use '-', ':', or parentheses). "
            + $"Offenders:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    private static bool IsGenerated(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.EndsWith(".g.cs", StringComparison.Ordinal)
        || file.EndsWith(".razor.g.cs", StringComparison.Ordinal);

    // Tracks comments and literals in one pass and reports the prohibited character only inside
    // string literals.
    private static bool LineHasEmDashInStringLiteral(string line, ref bool inBlockComment)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("@*", StringComparison.Ordinal)) return false; // Razor comment line

        var inStr = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                i = ReadInBlockComment(line, i, ref inBlockComment);
                continue;
            }
            if (inStr)
            {
                var (last, closed, emDash) = ReadInString(line, i);
                if (emDash) return true;
                inStr = !closed;
                i = last;
                continue;
            }
            i = ReadInCode(line, i, ref inStr, ref inBlockComment);
            if (i < 0) return false; // rest is a line comment
        }
        return false;
    }

    private static bool PairAt(string line, int i, char first, char second) =>
        i + 1 < line.Length && line[i] == first && line[i + 1] == second;

    /// <summary>Inside a block comment: the index of the last character read, the comment closed on <c>*/</c>.</summary>
    private static int ReadInBlockComment(string line, int i, ref bool inBlockComment)
    {
        if (!PairAt(line, i, '*', '/')) return i;
        inBlockComment = false;
        return i + 1;
    }

    /// <summary>Inside a string literal: the last character read (an escape takes two), whether it closed, whether it is an em-dash.</summary>
    private static (int Last, bool Closed, bool EmDash) ReadInString(string line, int i)
    {
        var c = line[i];
        if (c == '\\' && i + 1 < line.Length) return (i + 1, false, false); // skip escaped char
        return (i, c == '"', c == EmDash);
    }

    /// <summary>In code: the last character read, or -1 when a line comment starts.</summary>
    private static int ReadInCode(string line, int i, ref bool inStr, ref bool inBlockComment)
    {
        if (line[i] == '"')
        {
            inStr = true;
            return i;
        }
        if (PairAt(line, i, '/', '/')) return -1;
        if (!PairAt(line, i, '/', '*')) return i;
        inBlockComment = true;
        return i + 1;
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
