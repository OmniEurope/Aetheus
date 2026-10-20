// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Reads the kernel's out-of-memory traces (PLAN-010 lot 1): the <c>oom_kill</c> counter of
/// <c>/proc/vmstat</c> and the "Killed process" lines of the kernel log that name each victim.
/// </summary>
public static partial class OomKillParser
{
    /// <summary>
    /// The <c>oom_kill</c> value of a <c>/proc/vmstat</c> text, or null when the line is absent
    /// (kernels before 4.13 do not count kills) or unreadable: unknown, never "0 kills".
    /// </summary>
    public static long? ParseVmstatOomKill(string? vmstat)
    {
        if (string.IsNullOrEmpty(vmstat)) return null;
        foreach (var line in vmstat.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("oom_kill ", StringComparison.Ordinal)) continue;
            return long.TryParse(trimmed["oom_kill ".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
        return null;
    }

    /// <summary>
    /// Victims named by kernel log records, oldest first, the last <see cref="OomKillReportDto.MaxVictims"/>.
    /// Accepts <c>/dev/kmsg</c> records (<c>6,1234,5678,-;Out of memory: Killed process ...</c>) as well
    /// as plain message lines; both the global ("Out of memory:") and the cgroup ("Memory cgroup out of
    /// memory:") kills print the same "Killed process PID (comm)" phrase.
    /// </summary>
    public static List<OomKillVictimDto> ParseVictims(IEnumerable<string> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var victims = new List<OomKillVictimDto>();
        foreach (var record in records)
        {
            var match = KilledProcess().Match(record);
            if (!match.Success
                || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || pid <= 0)
                continue;
            victims.Add(new OomKillVictimDto { Pid = pid, Process = match.Groups["comm"].Value });
        }
        return victims.Count <= OomKillReportDto.MaxVictims
            ? victims
            : victims.GetRange(victims.Count - OomKillReportDto.MaxVictims, OomKillReportDto.MaxVictims);
    }

    // comm is at most 15 bytes in the kernel; the bound keeps a garbled record from matching a whole line.
    [GeneratedRegex(@"Killed process (?<pid>\d{1,10}) \((?<comm>[^\r\n]{1,64}?)\)(?=[ ,]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex KilledProcess();
}
