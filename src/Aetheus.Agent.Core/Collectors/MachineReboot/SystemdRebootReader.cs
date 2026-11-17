// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Agent.Core.Collectors.MachineReboot;

/// <summary>
/// The reboots the host decides through systemd: the enabled timers of <c>/etc/systemd/system</c> whose service reboots
/// (<see cref="Timers"/>), and the reboot already scheduled on the running system, which systemd records in
/// <c>/run/systemd/shutdown/scheduled</c> (<see cref="ScheduledShutdown"/>). Only the units the administrator put in
/// <c>/etc/systemd/system</c> are read: a packaged unit does not reboot the host. A timer is listed when it is enabled
/// (linked in a <c>*.wants</c> directory), has an <c>OnCalendar=</c> this reader converts, and no random delay: with a
/// <c>RandomizedDelaySec=</c> the real time is unknown, so nothing is shown rather than a time that may be hours off.
/// </summary>
internal static class SystemdRebootReader
{
    internal const string UnitDirectory = "/etc/systemd/system";
    internal const string ScheduledFile = "/run/systemd/shutdown/scheduled";

    public static List<MachineRebootEntryDto> Timers(IMachineRebootHostFiles files, string? machineZoneId)
    {
        List<MachineRebootEntryDto> entries = [];
        var enabledDirectories = files.ListDirectories(UnitDirectory).Where(d => d.EndsWith(".wants", StringComparison.Ordinal)).ToList();
        foreach (var timerPath in files.ListFiles(UnitDirectory).Where(p => p.EndsWith(".timer", StringComparison.Ordinal)))
        {
            var timerName = Path.GetFileName(timerPath);
            if (!enabledDirectories.Any(d => files.FileExists($"{d}/{timerName}"))) continue;
            if (files.ReadText(timerPath) is not { } timerText) continue;
            var timer = UnitFile.Parse(timerText);
            if (timer.Values("Timer", "RandomizedDelaySec").Any(IsDelay)) continue;

            var serviceName = timer.Values("Timer", "Unit").LastOrDefault() ?? timerName[..^".timer".Length] + ".service";
            if (files.ReadText($"{UnitDirectory}/{serviceName}") is not { } serviceText) continue;
            var commands = UnitFile.Parse(serviceText).Values("Service", "ExecStart").Select(StripExecPrefixes);
            if (!RebootsAny(commands, out var conditional)) continue;

            foreach (var calendar in timer.Values("Timer", "OnCalendar"))
            {
                if (!OnCalendarConverter.TryConvert(calendar, out var cron, out var zone)) continue;
                if ((zone ?? machineZoneId) is not { } zoneId) continue;
                entries.Add(new MachineRebootEntryDto
                {
                    Source = MachineRebootSource.SystemdTimer,
                    CronExpression = cron,
                    TimeZoneId = zoneId,
                    Conditional = conditional,
                    Detail = timerPath
                });
            }
        }

        return entries;
    }

    /// <summary>The reboot systemd has scheduled on the running system (<c>shutdown -r +30</c>): a single instant. Halting
    /// or powering off, or a file whose instant is unreadable, gives nothing.</summary>
    public static MachineRebootEntryDto? ScheduledShutdown(IMachineRebootHostFiles files)
    {
        if (files.ReadText(ScheduledFile) is not { } text) return null;
        Dictionary<string, string> values = [];
        foreach (var line in text.Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0) values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (!values.TryGetValue("MODE", out var mode) || mode != "reboot") return null;
        if (!values.TryGetValue("USEC", out var usec)
            || !long.TryParse(usec, NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds)
            || microseconds <= 0
            || microseconds / 1000 > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return null;
        }

        return new MachineRebootEntryDto
        {
            Source = MachineRebootSource.ScheduledShutdown,
            AtUtc = DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000).UtcDateTime,
            Detail = ScheduledFile
        };
    }

    internal static bool RebootsAny(IEnumerable<string> commands, out bool conditional)
    {
        foreach (var command in commands)
        {
            if (RebootCommand.Reboots(command, out conditional)) return true;
        }

        conditional = false;
        return false;
    }

    // systemd lets a command start with - (ignore failure), @ (argv0), + ! !! (privileges) or : (no expansion).
    private static string StripExecPrefixes(string command) => command.TrimStart('-', '@', '+', '!', ':');

    // A delay of "0" or "0s" is none; anything else (a duration, an unreadable value) leaves the time unknown.
    private static bool IsDelay(string value) => value.Trim() is not ("" or "0" or "0s" or "0us" or "0ms");
}
