// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Servers;

/// <summary>Where a scheduled job of the machine itself was read from (recette R-27, option A of 2026-11-12). The value is
/// stored in the server's last report: never reorder.</summary>
public enum MachineJobSource
{
    /// <summary>A line of <c>/etc/crontab</c> (system format, with a user column).</summary>
    SystemCrontab = 0,

    /// <summary>A line of a file of <c>/etc/cron.d</c> (system format).</summary>
    CronDirectory = 1,

    /// <summary>A line of the crontab of a user, read in the spool directory when the agent may read it.</summary>
    UserCrontab = 2,

    /// <summary>The scripts of <c>/etc/cron.hourly|daily|weekly|monthly</c>, run by <c>run-parts</c> at the time
    /// <c>/etc/crontab</c> gives (or by anacron, then the time is only the earliest one).</summary>
    PeriodicDirectory = 3,

    /// <summary>A systemd timer of the agent's whitelist (<c>MachineTimerWhitelist</c>).</summary>
    SystemdTimer = 4
}

public static class MachineJobLimits
{
    /// <summary>Jobs one report may carry; also the multiplier that keeps the planner row id of a job unique.</summary>
    public const int MaxEntries = 128;
}

/// <summary>One scheduled job of the machine, as its agent read it. Never the whole command line (it may carry a secret):
/// only the program it runs. A time the agent could not establish is absent, never guessed: a job with neither
/// <see cref="CronExpression"/> nor <see cref="AtUtc"/> is not reported.</summary>
public sealed record MachineJobEntryDto
{
    public MachineJobSource Source { get; init; }

    /// <summary>What runs: the program of a cron command, the directory of a <c>run-parts</c> schedule, the timer unit.</summary>
    [StringLength(120)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The user the job runs as; null for a timer and for a job whose user is not written (<c>run-parts</c> by anacron).</summary>
    [StringLength(64)]
    public string? User { get; init; }

    /// <summary>Five fields (minutes first) read in <see cref="TimeZoneId"/>; null for a single instant.</summary>
    [StringLength(200)]
    public string? CronExpression { get; init; }

    /// <summary>The IANA zone <see cref="CronExpression"/> is read in: the machine's own, or the one the entry names.</summary>
    [StringLength(64)]
    public string? TimeZoneId { get; init; }

    /// <summary>The next instant of a timer that has no calendar expression, or whose real time carries a random delay.</summary>
    public DateTime? AtUtc { get; init; }

    /// <summary>True when the time is only the earliest one (a <c>run-parts</c> directory that anacron may run instead,
    /// a command guarded by a test).</summary>
    public bool Conditional { get; init; }

    /// <summary>The file or unit the job was read from.</summary>
    [StringLength(300)]
    public string Detail { get; init; } = string.Empty;
}

/// <summary>What the agent read of the jobs the host schedules itself. Null in a heartbeat means "not collected yet" (an
/// older agent, or the first beat after a start): the stored reading is then left alone.</summary>
public sealed record MachineJobsDataDto
{
    /// <summary>True when the agent read the host (Linux): an empty <see cref="Jobs"/> then means nothing is scheduled.
    /// False on a host with nothing to read (Windows).</summary>
    public bool Collected { get; init; }

    /// <summary>The IANA zone of the machine, when the agent could establish it; null otherwise (a job whose time would
    /// then be read in the machine's zone is not reported).</summary>
    [StringLength(64)]
    public string? TimeZoneId { get; init; }

    /// <summary>The lines and units the agent saw but could not turn into a time (a macro without time such as
    /// <c>@reboot</c>, an expression the cron engine rejects, a timer without calendar or next time, a zone it does not know).</summary>
    public int SkippedLines { get; init; }

    [MaxLength(MachineJobLimits.MaxEntries)]
    public List<MachineJobEntryDto> Jobs { get; init; } = [];
}
