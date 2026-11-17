// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors.MachineReboot;
using Aetheus.Shared.Components.Servers;
using Cronos;

namespace Aetheus.Agent.Core.Collectors.MachineJobs;

/// <summary>Collects the jobs of the crontab files <see cref="CronJobReader"/> hands it, and counts what it could not use.</summary>
internal sealed class CronJobAccumulator(IMachineRebootHostFiles files, string? machineZoneId)
{
    private readonly List<MachineJobEntryDto> _jobs = [];
    private readonly HashSet<string> _namedDirectories = new(StringComparer.Ordinal);
    private int _skipped;

    public void AddFile(string path, MachineJobSource source, bool hasUserColumn)
    {
        if (files.ReadText(path) is not { } text) return;
        foreach (var line in CrontabParser.Read(text, hasUserColumn, machineZoneId))
        {
            // A user crontab has no user column: the file is named after its user.
            AddLine(line, path, source, hasUserColumn ? line.User : Path.GetFileName(path));
        }
    }

    public CronJobsReading Finish()
    {
        // A directory that holds scripts but that no crontab line runs is left to anacron alone: no time of day to show.
        _skipped += CronCommand.PeriodicDirectories.Count(directory => !_namedDirectories.Contains(directory) && HasScripts(directory));
        return new CronJobsReading(_jobs, _skipped);
    }

    private void AddLine(CrontabLine line, string path, MachineJobSource source, string? user)
    {
        if (line.Fields is null || line.ZoneId is null || line.Fields.Length > 200 || line.ZoneId.Length > 64
            || !CronExpression.TryParse(line.Fields, CronFormat.Standard, out _))
        {
            _skipped++;
            return;
        }

        // A line that reboots the host is the reboot reader's: it is already in the planner.
        if (RebootCommand.Reboots(line.Command, out _)) return;

        var directory = CronCommand.PeriodicDirectoryOf(line.Command);
        if (directory is not null)
        {
            _namedDirectories.Add(directory);
            if (!HasScripts(directory)) return;
        }

        _jobs.Add(new MachineJobEntryDto
        {
            Source = directory is null ? source : MachineJobSource.PeriodicDirectory,
            Name = directory ?? CronCommand.ProgramOf(line.Command),
            User = user is { Length: > 0 } ? CronCommand.Truncate(user, 64) : null,
            CronExpression = line.Fields,
            TimeZoneId = line.ZoneId,
            // Debian's run-parts lines defer to anacron when it is installed: its time is then the earliest, not a certain one.
            Conditional = directory is not null && line.Command.Contains("anacron", StringComparison.Ordinal),
            Detail = CronCommand.Truncate(path, 300)
        });
    }

    private bool HasScripts(string directory) =>
        files.ListFiles(directory).Any(path => CrontabParser.IsRunPartsName(Path.GetFileName(path)));
}
