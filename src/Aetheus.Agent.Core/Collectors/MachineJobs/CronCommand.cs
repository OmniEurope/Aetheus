// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors.MachineJobs;

/// <summary>What a cron command line says, without running it: the program it runs (never its arguments, they may carry a
/// secret) and the <c>/etc/cron.hourly|daily|weekly|monthly</c> directory a <c>run-parts</c> command runs.</summary>
internal static partial class CronCommand
{
    internal static readonly string[] PeriodicDirectories = ["/etc/cron.hourly", "/etc/cron.daily", "/etc/cron.weekly", "/etc/cron.monthly"];

    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal) { "sudo", "env", "nohup", "nice", "ionice", "time", "exec" };
    // Shell plumbing that comes before the real program in "cd / && program ...": never the name of a job.
    private static readonly HashSet<string> Plumbing = new(StringComparer.Ordinal) { "cd", "test", "[", "[[", "true", "export", "set", "umask", "exit", "{", "(" };

    [GeneratedRegex(@"\s*(?:&&|\|\||;|\|)\s*")]
    private static partial Regex ConnectorPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex AssignmentPattern();

    /// <summary>The periodic directory a <c>run-parts</c> command runs, or null when the command is anything else.</summary>
    public static string? PeriodicDirectoryOf(string command)
    {
        var words = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim(';', ')', '}', '"', '\'')).ToList();
        if (!words.Any(w => w == "run-parts" || w.EndsWith("/run-parts", StringComparison.Ordinal))) return null;
        return PeriodicDirectories.FirstOrDefault(directory => words.Any(w => w.TrimEnd('/') == directory));
    }

    /// <summary>The program a cron command runs, without its arguments: the first word of the first segment that is not shell
    /// plumbing, wrappers, options and <c>NAME=value</c> prefixes skipped, reduced to its file name.</summary>
    public static string ProgramOf(string command)
    {
        foreach (var segment in ConnectorPattern().Split(command))
        {
            var words = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(w => Wrappers.Contains(w) || AssignmentPattern().IsMatch(w) || w.StartsWith('-')).ToList();
            if (words.Count == 0 || Plumbing.Contains(words[0])) continue;
            return Truncate(Path.GetFileName(words[0].TrimEnd('/')) is { Length: > 0 } name ? name : words[0], 120);
        }

        return Truncate(command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? command, 120);
    }

    public static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
