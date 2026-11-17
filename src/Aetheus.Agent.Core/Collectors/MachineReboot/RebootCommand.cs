// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors.MachineReboot;

/// <summary>
/// Tells whether a command line (a crontab command, a systemd <c>ExecStart</c>) reboots the host: <c>reboot</c>,
/// <c>shutdown -r</c> / <c>--reboot</c>, <c>systemctl reboot</c>, <c>telinit 6</c> / <c>init 6</c>, also behind <c>sudo</c>,
/// <c>env</c>, <c>nohup</c>, <c>nice</c> or <c>sh -c "..."</c>. The command is only read, never run. Halting or powering off is
/// not a reboot. The line is cut at <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and <c>|</c> without reading quotes: a connector
/// inside a quoted argument can only make the reader miss a reboot, never invent one.
/// </summary>
internal static partial class RebootCommand
{
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal) { "sudo", "env", "nohup", "nice", "ionice", "time", "exec" };
    // A test that guards what follows: the reboot happens only when it holds. A hint, not a proof.
    private static readonly HashSet<string> Guards = new(StringComparer.Ordinal) { "[", "[[", "test", "if", "grep", "egrep", "fgrep", "pgrep" };

    [GeneratedRegex("""^\s*(?:sudo\s+)?(?:\S*/)?(?:sh|bash|dash|zsh)\s+-c\s+(?<q>["'])(?<body>.*)\k<q>\s*$""")]
    private static partial Regex ShellPattern();

    [GeneratedRegex(@"\s*(?:&&|\|\||;|\|)\s*")]
    private static partial Regex ConnectorPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex AssignmentPattern();

    /// <summary>Whether <paramref name="commandLine"/> reboots the host; <paramref name="conditional"/> is true when a guard
    /// stands before the reboot.</summary>
    public static bool Reboots(string commandLine, out bool conditional)
    {
        conditional = false;
        // sh -c "..." wraps a whole command line: read what it runs, before the line is cut at its connectors.
        if (ShellPattern().Match(commandLine) is { Success: true } shell) return Reboots(shell.Groups["body"].Value, out conditional);

        var segments = ConnectorPattern().Split(commandLine.Trim());
        for (var index = 0; index < segments.Length; index++)
        {
            if (!SegmentReboots(segments[index])) continue;
            conditional = segments.Take(index).Any(IsGuard);
            return true;
        }

        return false;
    }

    private static bool IsGuard(string segment) => Words(segment) is [var first, ..] && Guards.Contains(first);

    private static bool SegmentReboots(string segment)
    {
        var words = Words(segment);
        if (words.Count == 0) return false;
        var args = words.Skip(1).ToList();
        return Path.GetFileName(words[0]) switch
        {
            "reboot" => !args.Any(a => a is "-w" or "--wtmp-only"),
            "shutdown" => !args.Contains("-c") && args.Any(a => a == "--reboot" || (a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal) && a.Contains('r'))),
            "systemctl" => args.Where(a => !a.StartsWith('-')).FirstOrDefault() == "reboot",
            "init" or "telinit" => args.Count > 0 && args[^1] == "6",
            _ => false
        };
    }

    /// <summary>The words of a segment without the leading assignments, the wrappers and their options.</summary>
    private static List<string> Words(string segment)
    {
        List<string> words = [.. segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];
        var start = 0;
        while (start < words.Count
            && (AssignmentPattern().IsMatch(words[start])
                || Wrappers.Contains(Path.GetFileName(words[start]))
                || (start > 0 && words[start].StartsWith('-') && Wrappers.Contains(Path.GetFileName(words[start - 1])))))
        {
            start++;
        }

        return words.GetRange(start, words.Count - start);
    }
}
