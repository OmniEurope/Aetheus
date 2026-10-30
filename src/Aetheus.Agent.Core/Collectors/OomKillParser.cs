// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Reads the kernel's out-of-memory traces (PLAN-010 lot 1): the <c>oom_kill</c> counter of
/// <c>/proc/vmstat</c> and the kernel log lines that name each victim, whether the host or a memory
/// cgroup (a container) ran out of memory, and how much memory the victim held.
/// </summary>
public static partial class OomKillParser
{
    private const int MaxCgroupLength = 256;
    private const int ContainerShortIdLength = 12;

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
    /// as plain message lines. For each kill the kernel prints a summary line
    /// (<c>oom-kill:constraint=CONSTRAINT_MEMCG,...,task_memcg=...,task=...,pid=...</c>) and then the
    /// "Killed process PID (comm)" line, introduced by "Memory cgroup out of memory:" for a cgroup limit and
    /// by "Out of memory:" for the host. The summary, when it names the same pid, states the kind and the
    /// cgroup; the phrase of the kill line is the fallback for the kind.
    /// </summary>
    public static List<OomKillVictimDto> ParseVictims(IEnumerable<string> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var victims = new List<OomKillVictimDto>();
        OomSummary? summary = null;
        foreach (var record in records)
        {
            var summaryMatch = SummaryConstraint().Match(record);
            if (summaryMatch.Success)
            {
                summary = ReadSummary(record, summaryMatch.Groups["constraint"].Value);
                continue;
            }

            var match = KilledProcess().Match(record);
            if (!match.Success
                || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || pid <= 0)
                continue;

            // A summary that names another pid belongs to a kill whose own line was lost: not this one's.
            var own = summary is { } candidate && (candidate.Pid is null || candidate.Pid == pid) ? candidate : (OomSummary?)null;
            summary = null;
            victims.Add(BuildVictim(record, match, pid, own));
        }
        return victims.Count <= OomKillReportDto.MaxVictims
            ? victims
            : victims.GetRange(victims.Count - OomKillReportDto.MaxVictims, OomKillReportDto.MaxVictims);
    }

    /// <summary>
    /// The 12-character short id (the one <c>docker ps</c> prints) of the docker container a cgroup path
    /// belongs to, from <c>/system.slice/docker-&lt;64 hex&gt;.scope</c> (systemd driver) or
    /// <c>/docker/&lt;64 hex&gt;</c> (cgroupfs driver); null for any other cgroup. The container name is not
    /// looked up here: the backend matches the id with the containers the agent lists.
    /// </summary>
    public static string? ContainerIdFromCgroup(string? cgroup)
    {
        if (string.IsNullOrEmpty(cgroup)) return null;
        var match = DockerCgroup().Match(cgroup);
        return match.Success ? match.Groups["id"].Value[..ContainerShortIdLength] : null;
    }

    private static OomKillVictimDto BuildVictim(string record, Match killed, int pid, OomSummary? summary)
    {
        var cgroup = summary?.TaskMemcg ?? summary?.OomMemcg;
        var memory = ReadMemory(record, killed.Index + killed.Length);
        return new OomKillVictimDto
        {
            Pid = pid,
            Process = killed.Groups["comm"].Value,
            Kind = summary?.Kind is { } kind and not OomKillKind.Unknown ? kind : KindFromPhrase(record),
            Cgroup = cgroup is null || cgroup.Length <= MaxCgroupLength ? cgroup : cgroup[..MaxCgroupLength],
            ContainerId = ContainerIdFromCgroup(summary?.OomMemcg) ?? ContainerIdFromCgroup(summary?.TaskMemcg),
            TotalVmKb = Field(memory, "total-vm"),
            AnonRssKb = Field(memory, "anon-rss"),
            FileRssKb = Field(memory, "file-rss"),
            ShmemRssKb = Field(memory, "shmem-rss")
        };
    }

    private static OomKillKind KindFromPhrase(string record) =>
        record.Contains("Memory cgroup out of memory", StringComparison.Ordinal) ? OomKillKind.Cgroup
        : record.Contains("Out of memory:", StringComparison.Ordinal) ? OomKillKind.Global
        : OomKillKind.Unknown;

    private static OomSummary ReadSummary(string record, string constraint)
    {
        int? pid = int.TryParse(SummaryPid().Match(record).Groups["v"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
        return new OomSummary(
            constraint switch
            {
                "CONSTRAINT_NONE" => OomKillKind.Global,
                "CONSTRAINT_MEMCG" => OomKillKind.Cgroup,
                "CONSTRAINT_CPUSET" => OomKillKind.Cpuset,
                "CONSTRAINT_MEMORY_POLICY" => OomKillKind.MemoryPolicy,
                _ => OomKillKind.Unknown
            },
            Text(SummaryTaskMemcg().Match(record)),
            Text(SummaryOomMemcg().Match(record)),
            pid);

        static string? Text(Match m) => m.Success ? m.Groups["v"].Value : null;
    }

    // A field the line does not print stays absent from the result: unknown, never 0.
    private static Dictionary<string, long> ReadMemory(string record, int start)
    {
        var fields = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (Match field in MemoryField().Matches(record, start))
        {
            if (long.TryParse(field.Groups["kb"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
                fields.TryAdd(field.Groups["key"].Value, kb);
        }
        return fields;
    }

    private static long? Field(Dictionary<string, long> fields, string key) =>
        fields.TryGetValue(key, out var kb) ? kb : null;

    private readonly record struct OomSummary(OomKillKind Kind, string? TaskMemcg, string? OomMemcg, int? Pid);

    // comm is at most 15 bytes in the kernel; the bound keeps a garbled record from matching a whole line.
    [GeneratedRegex(@"Killed process (?<pid>\d{1,10}) \((?<comm>[^\r\n]{1,64}?)\)(?=[ ,]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex KilledProcess();

    [GeneratedRegex(@"oom-kill:constraint=(?<constraint>CONSTRAINT_[A-Z_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryConstraint();

    [GeneratedRegex(@",pid=(?<v>\d{1,10})(?=,|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryPid();

    [GeneratedRegex(@",task_memcg=(?<v>[^,\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryTaskMemcg();

    [GeneratedRegex(@",oom_memcg=(?<v>[^,\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryOomMemcg();

    [GeneratedRegex(@"(?<key>total-vm|anon-rss|file-rss|shmem-rss):(?<kb>\d{1,15})kB", RegexOptions.CultureInvariant)]
    private static partial Regex MemoryField();

    [GeneratedRegex(@"(?:^|/)(?:docker-)?(?<id>[0-9a-f]{64})(?:\.scope)?(?:/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DockerCgroup();
}
