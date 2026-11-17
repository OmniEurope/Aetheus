// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors.MachineReboot;
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Agent.Core.Collectors.MachineJobs;

/// <summary>What the crontabs gave: the jobs, and the lines the reader saw but could not turn into a time.</summary>
internal sealed record CronJobsReading(List<MachineJobEntryDto> Jobs, int Skipped);

/// <summary>
/// The jobs of the system crontabs (<c>/etc/crontab</c>, <c>/etc/cron.d/*</c>), of the user crontabs the agent may read (the
/// spool directories, usually root-only: unreadable files read as nothing, no privilege is taken) and of the
/// <c>/etc/cron.hourly|daily|weekly|monthly</c> directories. Read only: files are opened for reading, never written.
/// A directory is not a line of its own: its schedule is the <c>run-parts</c> line of a crontab that names it (Debian's
/// <c>/etc/crontab</c>), reported once for the directory when it holds scripts, and conditional when that line defers to anacron
/// (anacron has no time of day). A directory with scripts and no such line has no time, and is counted as skipped.
/// Lines are read by <see cref="CrontabParser"/>; one the cron engine (<c>Cronos</c>) rejects, one with no time
/// (<c>@reboot</c>), one with no zone to read it in, and one the report format cannot carry are counted, never fatal.
/// A line that reboots the host is left to the reboot reader (R-11): it is already in the planner. See
/// <see cref="CronJobAccumulator"/> and <see cref="CronCommand"/>.
/// </summary>
internal static class CronJobReader
{
    public static CronJobsReading Read(IMachineRebootHostFiles files, string? machineZoneId)
    {
        var accumulator = new CronJobAccumulator(files, machineZoneId);
        accumulator.AddFile(CronRebootReader.SystemCrontab, MachineJobSource.SystemCrontab, hasUserColumn: true);
        foreach (var path in files.ListFiles(CronRebootReader.CronDirectory).Where(p => CrontabParser.IsRunPartsName(Path.GetFileName(p))))
        {
            accumulator.AddFile(path, MachineJobSource.CronDirectory, hasUserColumn: true);
        }

        foreach (var directory in CronRebootReader.UserCrontabDirectories)
        {
            foreach (var path in files.ListFiles(directory))
            {
                accumulator.AddFile(path, MachineJobSource.UserCrontab, hasUserColumn: false);
            }
        }

        return accumulator.Finish();
    }
}
