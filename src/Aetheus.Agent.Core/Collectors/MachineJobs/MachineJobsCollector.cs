// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Collectors.MachineReboot;
using Aetheus.Agent.Core.Services;
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Agent.Core.Collectors.MachineJobs;

/// <summary>Recette R-27: reads the jobs the host schedules by itself, and its time zone, so the planner can show what Aetheus
/// does not schedule. Read only, with the agent's own rights: no write, no sudo, argv only (no shell).</summary>
public interface IMachineJobsCollector
{
    Task<MachineJobsDataDto> CollectAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads the cron jobs (<see cref="CronJobReader"/>) and the whitelisted systemd timers (<see cref="SystemdTimerJobReader"/>)
/// and reports them with the machine's IANA zone. A source it cannot read contributes nothing; a line it cannot turn into a
/// time is counted in <see cref="MachineJobsDataDto.SkippedLines"/> and never fails the collection. More jobs than one report
/// may carry are cut (the timers first, they are the fewest) and the cut ones are counted as skipped. Windows has no cron: the
/// collector reports "not collected" and the backend keeps whatever it had. Not covered: <c>at</c> jobs, anacron's own
/// <c>/etc/anacrontab</c> (no time of day), a configuration management tool that runs jobs from outside, and a crontab or
/// unit the agent may not read.
/// </summary>
public sealed partial class MachineJobsCollector : IMachineJobsCollector
{
    private readonly IMachineRebootHostFiles _files;
    private readonly IShellRunner _shell;
    private readonly bool _isWindows;
    private readonly string? _machineZoneId;

    public MachineJobsCollector(IShellRunner shell)
        : this(new MachineRebootHostFiles(), shell, OperatingSystem.IsWindows(), ResolveZone(new MachineRebootHostFiles(), TimeZoneInfo.Local.Id)) { }

    internal MachineJobsCollector(IMachineRebootHostFiles files, IShellRunner shell, bool isWindows, string? machineZoneId)
    {
        _files = files;
        _shell = shell;
        _isWindows = isWindows;
        _machineZoneId = machineZoneId;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_+\-/]{1,64}$")]
    private static partial Regex ZonePattern();

    public async Task<MachineJobsDataDto> CollectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_isWindows) return new MachineJobsDataDto { Collected = false };

        var timers = await SystemdTimerJobReader.ReadAsync(_shell, _machineZoneId, ct).ConfigureAwait(false);
        var cron = CronJobReader.Read(_files, _machineZoneId);
        List<MachineJobEntryDto> jobs = [.. timers.Jobs, .. cron.Jobs];
        var skipped = timers.Skipped + cron.Skipped;
        if (jobs.Count > MachineJobLimits.MaxEntries)
        {
            skipped += jobs.Count - MachineJobLimits.MaxEntries;
            jobs = jobs[..MachineJobLimits.MaxEntries];
        }

        return new MachineJobsDataDto { Collected = true, TimeZoneId = _machineZoneId, SkippedLines = skipped, Jobs = jobs };
    }

    /// <summary>
    /// The IANA identifier of the host's zone. The runtime's own answer first; when it only knows the zone as "Local" (a
    /// <c>/etc/localtime</c> that is a copy, not a link), the first line of <c>/etc/timezone</c> (Debian). Null when neither
    /// gives a plain identifier: a job whose time would be read in the machine's zone is then not reported, never placed in a
    /// guessed zone.
    /// </summary>
    internal static string? ResolveZone(IMachineRebootHostFiles files, string runtimeZoneId)
    {
        if (!string.Equals(runtimeZoneId, "Local", StringComparison.OrdinalIgnoreCase) && ZonePattern().IsMatch(runtimeZoneId)) return runtimeZoneId;
        var fromFile = files.ReadText("/etc/timezone")?.Split('\n')[0].Trim();
        return fromFile is not null && ZonePattern().IsMatch(fromFile) ? fromFile : null;
    }
}
