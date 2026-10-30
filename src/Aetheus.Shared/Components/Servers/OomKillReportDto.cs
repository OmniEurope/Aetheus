// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Servers;

/// <summary>
/// Out-of-memory kills of the host since its last boot (PLAN-010 lot 1). Sent by Linux agents only;
/// a heartbeat without it (Windows, an older agent, an unreadable <c>/proc/vmstat</c>) is unknown and
/// never raises anything.
/// </summary>
public sealed record OomKillReportDto
{
    /// <summary>Most victims the agent reports: the backend names at most this many per alert.</summary>
    public const int MaxVictims = 16;

    /// <summary><c>/proc/sys/kernel/random/boot_id</c>: tells a reboot (counter back to 0) from a new kill.</summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string BootId { get; init; } = string.Empty;

    /// <summary>The kernel's <c>oom_kill</c> counter of <c>/proc/vmstat</c>: kills since the boot.</summary>
    [Range(0, long.MaxValue)]
    public long KillCount { get; init; }

    /// <summary>
    /// Whether the agent could read the kernel log at its last attempt, which names the killed
    /// process, from <c>/dev/kmsg</c> or from the systemd journal (the unit is in the <c>systemd-journal</c>
    /// group). False on an install from before that group was granted, or without systemd: the backend
    /// then reports the count without a name.
    /// </summary>
    public bool VictimsReadable { get; init; }

    /// <summary>Latest kills found in the kernel log, oldest first; empty when it was not readable.</summary>
    [MaxLength(MaxVictims)]
    public List<OomKillVictimDto> RecentVictims { get; init; } = [];
}

/// <summary>One process the kernel killed for lack of memory, as its log line names it.</summary>
public sealed record OomKillVictimDto
{
    [Range(1, int.MaxValue)]
    public int Pid { get; init; }

    /// <summary>The task's command name (<c>comm</c>, 15 characters in the kernel).</summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string Process { get; init; } = string.Empty;

    // Everything below is optional: an older agent does not send it and the backend then words the
    // alert as before (Kind Unknown, no container, no memory).

    /// <summary>Whether a memory cgroup (a container) or the whole host ran out of memory.</summary>
    public OomKillKind Kind { get; init; }

    /// <summary>The cgroup of the killed task (<c>task_memcg</c>), e.g. a docker scope.</summary>
    [StringLength(256)]
    public string? Cgroup { get; init; }

    /// <summary>The 12-character short id of the docker container whose scope is <see cref="Cgroup"/>;
    /// null when the cgroup is not a docker scope. The agent does not resolve the name: the backend
    /// matches it with the containers of the same heartbeat.</summary>
    [StringLength(64)]
    public string? ContainerId { get; init; }

    /// <summary>Virtual size of the killed process, in kB (<c>total-vm</c>).</summary>
    [Range(0, long.MaxValue)]
    public long? TotalVmKb { get; init; }

    /// <summary>Anonymous resident memory of the killed process, in kB (<c>anon-rss</c>).</summary>
    [Range(0, long.MaxValue)]
    public long? AnonRssKb { get; init; }

    /// <summary>File-backed resident memory of the killed process, in kB (<c>file-rss</c>).</summary>
    [Range(0, long.MaxValue)]
    public long? FileRssKb { get; init; }

    /// <summary>Shared-memory resident memory of the killed process, in kB (<c>shmem-rss</c>).</summary>
    [Range(0, long.MaxValue)]
    public long? ShmemRssKb { get; init; }
}
