// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Servers;

/// <summary>Where a reboot the machine decides by itself comes from (recette R-11, option A of 2026-11-09). The value is
/// stored in the server's last report: never reorder.</summary>
public enum MachineRebootSource
{
    /// <summary><c>Unattended-Upgrade::Automatic-Reboot</c> of the unattended-upgrades package (apt configuration).</summary>
    UnattendedUpgrades = 0,

    /// <summary>A cron entry whose command reboots the host (<c>reboot</c>, <c>shutdown -r</c>, <c>systemctl reboot</c>).</summary>
    Cron = 1,

    /// <summary>An enabled systemd timer whose service reboots the host.</summary>
    SystemdTimer = 2,

    /// <summary>A reboot already scheduled on the running system (<c>shutdown -r</c>, systemd's <c>scheduled</c> file).</summary>
    ScheduledShutdown = 3
}

public static class MachineRebootLimits
{
    /// <summary>Entries one report may carry; also the multiplier that keeps the planner row id of an entry unique.</summary>
    public const int MaxEntries = 32;
}

/// <summary>One reboot the machine decides by itself, as its agent read it. A time the agent could not establish is absent,
/// never guessed: an entry with neither <see cref="CronExpression"/> nor <see cref="AtUtc"/> (unattended-upgrades set to
/// reboot "now", right after an upgrade) has no time of day and is never drawn.</summary>
public sealed record MachineRebootEntryDto
{
    public MachineRebootSource Source { get; init; }

    /// <summary>Five fields (minutes first) read in <see cref="TimeZoneId"/>; null for a single instant or an entry with no time.</summary>
    [StringLength(200)]
    public string? CronExpression { get; init; }

    /// <summary>The IANA zone <see cref="CronExpression"/> is read in: the machine's own, or the one the entry names.</summary>
    [StringLength(64)]
    public string? TimeZoneId { get; init; }

    /// <summary>The instant of a reboot already scheduled on the running system.</summary>
    public DateTime? AtUtc { get; init; }

    /// <summary>True when the reboot only happens if something else holds (unattended-upgrades reboots only after an upgrade
    /// that needs it; a cron command guarded by a test): the time is the earliest one, not a certain one.</summary>
    public bool Conditional { get; init; }

    /// <summary>The file or unit the entry was read from.</summary>
    [StringLength(300)]
    public string Detail { get; init; } = string.Empty;
}

/// <summary>What the agent read of the reboots the host schedules itself. Null in a heartbeat means "not collected yet" (an
/// older agent, or the first beat after a start): the stored reading is then left alone.</summary>
public sealed record MachineRebootDataDto
{
    /// <summary>True when the agent read the host (Linux): an empty <see cref="Entries"/> then means nothing is scheduled.
    /// False on a host with nothing to read (Windows).</summary>
    public bool Collected { get; init; }

    [MaxLength(MachineRebootLimits.MaxEntries)]
    public List<MachineRebootEntryDto> Entries { get; init; } = [];
}
