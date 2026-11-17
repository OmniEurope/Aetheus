// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Agent.Core.Collectors.MachineReboot;

/// <summary>
/// The cron entries that reboot the host, read from <c>/etc/crontab</c>, <c>/etc/cron.d/*</c> (system format, with a user
/// column) and the user crontabs the agent may read (no user column; they are usually root-only, then nothing is read, and no
/// privilege is taken to read them). The lines are read by <see cref="CrontabParser"/>: the five time fields are kept as
/// written and read in the machine's zone, or in the one a <c>CRON_TZ=</c> line names; <c>@reboot</c> has no time and is
/// ignored. Whether the fields are valid is the planner's to decide, which shows nothing for an expression it cannot read.
/// </summary>
internal static class CronRebootReader
{
    internal const string SystemCrontab = "/etc/crontab";
    internal const string CronDirectory = "/etc/cron.d";
    internal static readonly string[] UserCrontabDirectories = ["/var/spool/cron/crontabs", "/var/spool/cron"];

    public static List<MachineRebootEntryDto> Read(IMachineRebootHostFiles files, string? machineZoneId)
    {
        List<MachineRebootEntryDto> entries = [];
        AddFile(entries, files, SystemCrontab, hasUserColumn: true, machineZoneId);
        foreach (var path in files.ListFiles(CronDirectory).Where(p => CrontabParser.IsRunPartsName(Path.GetFileName(p))))
        {
            AddFile(entries, files, path, hasUserColumn: true, machineZoneId);
        }

        foreach (var directory in UserCrontabDirectories)
        {
            foreach (var path in files.ListFiles(directory))
            {
                AddFile(entries, files, path, hasUserColumn: false, machineZoneId);
            }
        }

        return entries;
    }

    private static void AddFile(List<MachineRebootEntryDto> entries, IMachineRebootHostFiles files, string path, bool hasUserColumn, string? machineZoneId)
    {
        if (files.ReadText(path) is not { } text) return;
        foreach (var line in CrontabParser.Read(text, hasUserColumn, machineZoneId))
        {
            if (line.Fields is null) continue;
            if (!RebootCommand.Reboots(line.Command, out var conditional)) continue;
            if (line.ZoneId is null) continue;
            entries.Add(new MachineRebootEntryDto
            {
                Source = MachineRebootSource.Cron,
                CronExpression = line.Fields,
                TimeZoneId = line.ZoneId,
                Conditional = conditional,
                Detail = path
            });
        }
    }
}
