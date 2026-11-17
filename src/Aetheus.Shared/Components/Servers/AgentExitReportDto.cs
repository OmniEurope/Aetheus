// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Servers;

/// <summary>
/// How the agent process that ran before the current one ended, reported once with the first successful
/// heartbeat of the new process. Optional on the heartbeat: an older agent never sends it and an older
/// backend ignores it, so neither side breaks the other.
/// </summary>
public sealed record AgentExitReportDto
{
    /// <summary>Longest reason kept; the agent truncates, the backend refuses a longer one.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>One of <see cref="AgentExitKinds"/>.</summary>
    [Required]
    [StringLength(32, MinimumLength = 1)]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Why the process ended, in the agent's words (watchdog message, exception, stop signal).</summary>
    [StringLength(MaxReasonLength)]
    public string? Reason { get; init; }

    /// <summary>UTC instant the exit was recorded; null for <see cref="AgentExitKinds.Unclean"/>, where
    /// nothing recorded it.</summary>
    public DateTime? OccurredAtUtc { get; init; }

    /// <summary><c>/proc/sys/kernel/random/boot_id</c> read while the previous process was alive; null off Linux.</summary>
    [StringLength(64)]
    public string? PreviousBootId { get; init; }

    /// <summary>The same boot id read by the process that reports. Different from
    /// <see cref="PreviousBootId"/> means the host rebooted in between; null off Linux.</summary>
    [StringLength(64)]
    public string? BootId { get; init; }
}

/// <summary>The ways an agent process can end, as <see cref="AgentExitReportDto.Kind"/>.</summary>
public static class AgentExitKinds
{
    /// <summary>The agent terminated itself on purpose (liveness watchdog, self-update).</summary>
    public const string Restart = nameof(Restart);

    /// <summary>The host asked the process to stop (SIGTERM, service stop).</summary>
    public const string Stopped = nameof(Stopped);

    /// <summary>An unhandled exception ended the process.</summary>
    public const string Crash = nameof(Crash);

    /// <summary>The previous process left no exit record: killed, out of memory, crash without a
    /// handler or power loss.</summary>
    public const string Unclean = nameof(Unclean);
}
