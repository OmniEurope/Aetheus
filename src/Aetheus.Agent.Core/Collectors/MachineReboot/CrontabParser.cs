// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors.MachineReboot;

/// <summary>One command line of a crontab. <see cref="Fields"/> is null when the line has no time the reader can use (a macro
/// without a time such as <c>@reboot</c>, a malformed line); <see cref="User"/> is null in a user crontab (no user column).</summary>
internal sealed record CrontabLine(string? Fields, string Command, string? User, string? ZoneId);

/// <summary>
/// Reads the command lines of a crontab (system format with a user column, or user format without), shared by the reboot
/// reader (R-11) and the jobs reader (R-27). The five time fields are kept as written and read in the machine's zone, or in
/// the one a <c>CRON_TZ=</c> line names; the macros <c>@hourly</c>..<c>@yearly</c> are expanded. Whether the fields are valid
/// is the caller's to decide. Comments and <c>NAME=value</c> lines are not command lines and are never returned.
/// </summary>
internal static partial class CrontabParser
{
    private static readonly Dictionary<string, string> Macros = new(StringComparer.Ordinal)
    {
        ["@hourly"] = "0 * * * *",
        ["@daily"] = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
        ["@weekly"] = "0 0 * * 0",
        ["@monthly"] = "0 0 1 * *",
        ["@yearly"] = "0 0 1 1 *",
        ["@annually"] = "0 0 1 1 *"
    };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\s*=")]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex(@"^CRON_TZ\s*=\s*""?([A-Za-z0-9_+\-/]+)""?\s*$")]
    private static partial Regex CronTimeZonePattern();

    [GeneratedRegex(@"^[0-9A-Za-z*,/\-?]+$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex RunPartsName();

    /// <summary>Whether a file name is one <c>run-parts</c> runs (letters, digits, underscore, hyphen: no dot, so no
    /// <c>.dpkg-old</c>, no <c>.placeholder</c>).</summary>
    public static bool IsRunPartsName(string fileName) => RunPartsName().IsMatch(fileName);

    public static IEnumerable<CrontabLine> Read(string text, bool hasUserColumn, string? machineZoneId)
    {
        var zone = machineZoneId;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (CronTimeZonePattern().Match(line) is { Success: true } named)
            {
                zone = named.Groups[1].Value;
                continue;
            }

            if (AssignmentPattern().IsMatch(line)) continue;
            yield return Parse(line, hasUserColumn, zone);
        }
    }

    /// <summary>The five time fields, the user and the command of a crontab line; a line with null fields when it has no
    /// time the reader can use (the command is then the whole line).</summary>
    private static CrontabLine Parse(string line, bool hasUserColumn, string? zone)
    {
        var unusable = new CrontabLine(null, line, null, zone);
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string fields;
        int next;
        if (words[0].StartsWith('@'))
        {
            if (!Macros.TryGetValue(words[0], out fields!)) return unusable;
            next = 1;
        }
        else
        {
            if (words.Length < 6 || !words.Take(5).All(w => FieldPattern().IsMatch(w))) return unusable;
            fields = string.Join(' ', words.Take(5));
            next = 5;
        }

        string? user = null;
        if (hasUserColumn)
        {
            if (next >= words.Length) return unusable;
            user = words[next++];
        }

        if (next >= words.Length) return unusable;
        // The command is the rest of the line, its words joined by single spaces (only its words are read).
        return new CrontabLine(fields, string.Join(' ', words.Skip(next)), user, zone);
    }
}
