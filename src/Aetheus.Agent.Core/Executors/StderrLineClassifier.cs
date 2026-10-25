// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Executors;

/// <summary>
/// Gives a line read from a process's standard error the level it deserves (recette R-36). Standard error is
/// an output channel, not a severity: <c>docker compose</c> writes "Volume x Creating", <c>jscpd</c> writes
/// "Using config from ...", <c>trivy</c> writes "INFO Detected OS family=..." there, and none of them is an
/// error. The rule has no list of tools to maintain: a line is an <see cref="TaskLogLevel.Error"/> only when
/// it states it (a leading ERROR / FATAL / PANIC token, "level=error", a compiler "error CS1234:"), a line
/// that states another severity gets that one, and every other line is a <see cref="TaskLogLevel.Warning"/>:
/// visible, never red, never dropped. The step's exit code, not its stderr, says whether it failed.
/// </summary>
internal static class StderrLineClassifier
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // ESC [ ... letter: colour codes a tool prints around its level word.
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.CultureInvariant, MatchTimeout);

    // A severity written as a key: level=error, "level":"warn", severity: fatal.
    private static readonly Regex LevelKey = new(
        @"(?<![A-Za-z0-9_])[""']?(?:level|lvl|severity)[""']?\s*[=:]\s*[""']?(?<word>[A-Za-z]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        MatchTimeout);

    // A severity word that opens the line, after an optional timestamp and bracket: "ERROR x", "[warn] x",
    // "2026-10-22T10:00:00Z  INFO  x", "fatal: x", "npm ERR! x", "E: x".
    private static readonly Regex LeadingWord = new(
        @"^\s*(?:\[?\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?\]?\s+"
        + @"|\[?\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\]?\s+)?"
        + @"(?<npm>npm\s+)?\[?(?<word>[A-Za-z]{1,9})(?<end>\]|:|!|\s|$)",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    // A compiler diagnostic anywhere in the line: "Program.cs(3,5): error CS1002: ; expected".
    private static readonly Regex CompilerDiagnostic = new(
        @":\s*(?<word>error|warning)\s+[A-Za-z]{2,}\d+\s*:",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        MatchTimeout);

    /// <summary>The level of one standard-error line: the one it states, else a warning.</summary>
    public static TaskLogLevel Classify(string line)
    {
        var text = AnsiEscape.Replace(line, string.Empty);

        var key = LevelKey.Match(text);
        if (key.Success && Map(key.Groups["word"].Value) is { } keyed)
            return keyed;

        var lead = LeadingWord.Match(text);
        if (lead.Success)
        {
            var word = lead.Groups["word"].Value;
            var end = lead.Groups["end"].Value;
            // "npm error x" and "npm warn x" name their level in lower case after the tool's own prefix.
            if (Map(word) is { } leading && (lead.Groups["npm"].Success || IsStatedAsSeverity(word, end, leading)))
                return leading;
        }

        var diagnostic = CompilerDiagnostic.Match(text);
        if (diagnostic.Success && Map(diagnostic.Groups["word"].Value) is { } compiled)
            return compiled;

        return TaskLogLevel.Warning;
    }

    // "error: x" and "[warn] x" state a severity in any case; a bare "ERROR x" does only in capitals, so that
    // the sentence "Errors are ignored" or "info about the build" is not taken for one. A capitalised "Error x"
    // or "Fatal x" still counts (Docker's "Error response from daemon"), a capitalised "Warning x" does not.
    private static bool IsStatedAsSeverity(string word, string end, TaskLogLevel level)
    {
        if (word.Length == 1)
            return end == ":"; // apt's "E: ..." and "W: ...", never a stray capital letter
        if (end is "]" or ":" or "!")
            return true;
        if (word.All(char.IsUpper))
            return true;
        return level == TaskLogLevel.Error && char.IsUpper(word[0]) && word.Skip(1).All(char.IsLower);
    }

    private static TaskLogLevel? Map(string word) => word.ToUpperInvariant() switch
    {
        "ERROR" or "ERR" or "FATAL" or "CRITICAL" or "CRIT" or "PANIC" or "EMERG" or "E" => TaskLogLevel.Error,
        "WARN" or "WARNING" or "WRN" or "W" => TaskLogLevel.Warning,
        "INFO" or "INF" or "NOTICE" => TaskLogLevel.Info,
        "DEBUG" or "DBG" or "TRACE" => TaskLogLevel.Debug,
        _ => null
    };
}
