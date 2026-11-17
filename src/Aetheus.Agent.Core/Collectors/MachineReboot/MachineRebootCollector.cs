// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Agent.Core.Collectors.MachineReboot;

/// <summary>Recette R-11: reads the reboots the host decides by itself, so the planner can show what Aetheus does not
/// schedule. Read only, with the agent's own rights, no shell, no sudo, no write.</summary>
public interface IMachineRebootCollector
{
    Task<MachineRebootDataDto> CollectAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads, in this order: the reboot of unattended-upgrades (<see cref="UnattendedUpgrades"/>), the reboot already scheduled
/// on the running system, the cron entries and the enabled systemd timers that reboot. A source it cannot read, or whose
/// configuration it cannot settle, contributes nothing: the planner shows a time only when the agent read one. Not covered:
/// <c>at</c> jobs, the <c>cron.hourly/daily/weekly/monthly</c> scripts (their time is the cron daemon's or anacron's), a
/// configuration management tool that reboots from outside, and a unit or crontab the agent may not read.
/// </summary>
public sealed class MachineRebootCollector : IMachineRebootCollector
{
    private readonly IMachineRebootHostFiles _files;
    private readonly bool _isWindows;
    private readonly string? _machineZoneId;

    public MachineRebootCollector() : this(new MachineRebootHostFiles(), OperatingSystem.IsWindows(), MachineZone()) { }

    internal MachineRebootCollector(IMachineRebootHostFiles files, bool isWindows, string? machineZoneId)
    {
        _files = files;
        _isWindows = isWindows;
        _machineZoneId = machineZoneId;
    }

    public Task<MachineRebootDataDto> CollectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_isWindows) return Task.FromResult(new MachineRebootDataDto { Collected = false });

        List<MachineRebootEntryDto> entries = [];
        if (UnattendedUpgrades() is { } unattended) entries.Add(unattended);
        if (SystemdRebootReader.ScheduledShutdown(_files) is { } scheduled) entries.Add(scheduled);
        entries.AddRange(CronRebootReader.Read(_files, _machineZoneId));
        entries.AddRange(SystemdRebootReader.Timers(_files, _machineZoneId));
        return Task.FromResult(new MachineRebootDataDto
        {
            Collected = true,
            Entries = [.. entries.Take(MachineRebootLimits.MaxEntries)]
        });
    }

    /// <summary>
    /// <c>Unattended-Upgrade::Automatic-Reboot</c> (apt documents it as off by default) with the unattended upgrades enabled
    /// (<c>APT::Periodic::Unattended-Upgrade</c> above zero, off by default). <c>Automatic-Reboot-Time</c> gives the time of
    /// day (<c>HH:MM</c>, the machine's zone); without it apt reboots at once after an upgrade that needs it ("now"), which
    /// has no time of day: the entry is reported with no time. A time that is not <c>HH:MM</c> reports nothing. The reboot
    /// only happens after an upgrade that asks for it, so the entry is always conditional.
    /// </summary>
    private MachineRebootEntryDto? UnattendedUpgrades()
    {
        var options = AptConfigReader.Read(_files);
        if (!options.TryGetValue("Unattended-Upgrade::Automatic-Reboot", out var reboot)
            || AptConfigReader.AsBoolean(reboot.Value) is not true)
        {
            return null;
        }

        if (!options.TryGetValue("APT::Periodic::Unattended-Upgrade", out var periodic)
            || !int.TryParse(periodic.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            || days <= 0)
        {
            return null;
        }

        var entry = new MachineRebootEntryDto { Source = MachineRebootSource.UnattendedUpgrades, Conditional = true, Detail = reboot.File };
        if (!options.TryGetValue("Unattended-Upgrade::Automatic-Reboot-Time", out var time)
            || string.Equals(time.Value.Trim(), "now", StringComparison.OrdinalIgnoreCase))
        {
            return entry;
        }

        if (!TryClock(time.Value.Trim(), out var hour, out var minute) || _machineZoneId is null)
        {
            return null;
        }

        return entry with
        {
            CronExpression = string.Create(CultureInfo.InvariantCulture, $"{minute} {hour} * * *"),
            TimeZoneId = _machineZoneId
        };
    }

    // apt reads the time with strptime("%H:%M"): one or two digits on each side.
    private static bool TryClock(string text, out int hour, out int minute)
    {
        hour = minute = 0;
        var parts = text.Split(':');
        return parts.Length == 2
            && parts.All(part => part.Length is 1 or 2 && part.All(char.IsAsciiDigit))
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour) && hour <= 23
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute) && minute <= 59;
    }

    /// <summary>The IANA identifier of the host's zone; null when the runtime only knows it as "Local" (no zone to read a time in).</summary>
    private static string? MachineZone()
    {
        var id = TimeZoneInfo.Local.Id;
        return id.Length == 0 || string.Equals(id, "Local", StringComparison.OrdinalIgnoreCase) ? null : id;
    }
}
