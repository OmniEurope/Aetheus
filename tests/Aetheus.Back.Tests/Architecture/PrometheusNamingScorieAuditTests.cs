// SPDX-License-Identifier: EUPL-1.2
using System.IO;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// S-TECH-PRMZ: the project was renamed Prometheus -> Aetheus. The sudoers <c>Cmnd_Alias</c> prefix
/// <c>PROM_*</c> (and any live <c>PROM_</c> identifier / env var) is a naming scorie of the pre-rename
/// agent and must never reappear in production source. This guard scans the shell installers and the
/// backend/agent/front C# + Razor sources for a live <c>PROM_</c> token and fails the build if one
/// resurfaces.
///
/// Deliberately narrow to avoid false positives: it inspects only <b>non-comment</b> lines (shell
/// <c>#</c> comments, C#/Razor <c>//</c> / <c>///</c> / <c>/* */</c>), so the single legitimate
/// reference - the migration-cleanup routine's comment naming the OLD <c>PROM_*</c> aliases it removes
/// (<c>install-agent-linux.sh</c> <c>cleanup_legacy_prometheus_artifacts</c>) - is allowed. A word
/// boundary before <c>PROM_</c> keeps unrelated tokens (e.g. <c>EEPROM_</c>) from matching.
/// </summary>
public sealed class PrometheusNamingScorieAuditTests
{
    private static readonly Regex PromToken = new(@"(?<![A-Za-z0-9])PROM_", RegexOptions.Compiled);

    [Fact]
    public void No_live_PROM_alias_or_identifier_survives_the_rebrand()
    {
        var repoRoot = FindRepoRoot();
        var roots = new[]
        {
            (Path.Combine(repoRoot, "deploy"), new[] { "*.sh" }),
            (Path.Combine(repoRoot, "src"), new[] { "*.cs", "*.razor" }),
        };

        var violations = new List<string>();
        foreach (var (dir, patterns) in roots)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pattern in patterns)
                foreach (var file in RepositoryScan.Enumerate(dir, pattern))
                {
                    if (IsGenerated(file)) continue;
                    var isShell = file.EndsWith(".sh", StringComparison.Ordinal);
                    var inBlockComment = false;
                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                        if (LineHasLivePromToken(lines[i], isShell, ref inBlockComment))
                            violations.Add($"  - {Path.GetRelativePath(repoRoot, file)}:{i + 1}");
                }
        }

        Assert.True(violations.Count == 0,
            "Live PROM_ token (Prometheus naming scorie) found in production source - rename to AETHEUS_. "
            + $"Offenders:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    private static bool IsGenerated(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.EndsWith(".g.cs", StringComparison.Ordinal)
        || file.EndsWith(".razor.g.cs", StringComparison.Ordinal);

    // Reports a PROM_ token only when it sits on executable (non-comment) content. Shell uses '#'
    // line comments; C#/Razor use '//', '///' and '/* */' block comments.
    private static bool LineHasLivePromToken(string line, bool isShell, ref bool inBlockComment)
    {
        // Escape hatch (challenge follow-up): the guard bans the pre-rename Prometheus->Aetheus scorie, but
        // a future *legitimate* Prometheus-the-monitoring-tool integration would also use PROM_ env vars
        // (PROM_SCRAPE_INTERVAL, PROM_REMOTE_WRITE_URL...). An explicit `prometheus-ok` marker on the line
        // whitelists such an intentional usage without disabling the guard for accidental scorie.
        if (line.Contains("prometheus-ok", StringComparison.OrdinalIgnoreCase)) return false;

        // Shell and C# sources have separate readers.
        return isShell
            ? PromToken.IsMatch(ShellCode(line))
            : PromToken.IsMatch(CodeOutsideComments(line, ref inBlockComment));
    }

    /// <summary>A shell line without its comment: nothing for a comment line, the text before an unquoted <c>#</c> otherwise.</summary>
    private static string ShellCode(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#')) return string.Empty;
        // Strip an inline '#' comment (best-effort: '#' not inside a quoted string). Good enough for
        // sudoers/heredoc lines, where PROM_ would appear as a live alias, not in a quoted literal.
        var hashIdx = IndexOfUnquoted(line, '#');
        return hashIdx >= 0 ? line[..hashIdx] : line;
    }

    /// <summary>A C#/Razor line without its <c>//</c> comment and the parts inside <c>/* */</c> block comments.</summary>
    private static string CodeOutsideComments(string line, ref bool inBlockComment)
    {
        var buffer = new System.Text.StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inBlockComment)
            {
                if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break; // rest is a line comment
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlockComment = true; i++; continue; }
            buffer.Append(c);
        }
        return buffer.ToString();
    }

    private static int IndexOfUnquoted(string line, char target)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == target) return i;
        }
        return -1;
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
