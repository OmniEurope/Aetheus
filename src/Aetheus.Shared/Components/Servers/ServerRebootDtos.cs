// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using Aetheus.Shared.Components.Tasks;

namespace Aetheus.Shared.Components.Servers;

/// <summary>Recette R-8 (option B of 2026-11-07): what a reboot order carries to the agent, and the bounds every side
/// applies to it. The order is a typed task of the server-action pipeline (<see cref="OperationKind.ServerReboot"/>);
/// these values ride in its environment.</summary>
public static class ServerRebootOrder
{
    /// <summary>The schedule version the order was emitted for: the agent's confirmation fails once it changed.</summary>
    public const string ScheduleVersionEnvVar = "AETHEUS_REBOOT_SCHEDULE_VERSION";

    /// <summary>The occurrence the order serves, ISO 8601 UTC.</summary>
    public const string OccurrenceEnvVar = "AETHEUS_REBOOT_OCCURRENCE_UTC";

    /// <summary>The moment past which the agent refuses the order on its own, ISO 8601 UTC.</summary>
    public const string NotAfterEnvVar = "AETHEUS_REBOOT_NOT_AFTER_UTC";

    /// <summary>The time zone that means "UTC" in a schedule.</summary>
    public const string UtcTimeZone = "UTC";

    /// <summary>How long an occurrence may wait for its server to be idle before it is skipped; also how long an order
    /// stays valid after its occurrence.</summary>
    public static readonly TimeSpan MaxPostponement = TimeSpan.FromMinutes(60);

    /// <summary>Duration the planner draws for a reboot: the host is away a few minutes.</summary>
    public const int PlannedMinutes = 5;
}

/// <summary>What the scheduler last did for a schedule.</summary>
public enum RebootScheduleOutcome
{
    /// <summary>No occurrence handled yet.</summary>
    None = 0,

    /// <summary>The reboot order was handed to the agent; the task says how it ended.</summary>
    Ordered = 1,

    /// <summary>The occurrence waits for the server to be idle (<see cref="ServerRebootScheduleDto.OutcomeDetail"/> names
    /// what runs).</summary>
    Postponed = 2,

    /// <summary>The occurrence was given up; <see cref="ServerRebootScheduleDto.SkipReason"/> says why.</summary>
    Skipped = 3
}

public enum RebootSkipReason
{
    None = 0,

    /// <summary>Something still ran on the server when the postponement ran out.</summary>
    ServerBusy = 1,

    /// <summary>Aetheus did not run at the time of the occurrence (stopped or restarting) and its window is over.</summary>
    Missed = 2
}

/// <summary>Why the backend refuses to confirm a reboot order to the agent.</summary>
public enum RebootOrderRefusal
{
    None = 0,
    NotARebootOrder = 1,
    ScheduleRemoved = 2,
    ScheduleDisabled = 3,
    ScheduleChanged = 4,
    NotTheCurrentOrder = 5,
    ServerBusy = 6,
    Expired = 7
}

/// <summary>The backend's answer to an agent about to execute a reboot order.</summary>
public sealed record RebootOrderConfirmationDto
{
    public bool Allowed { get; init; }
    public RebootOrderRefusal Refusal { get; init; }
}

/// <summary>A server's reboot schedule as the server page and the planner show it.</summary>
public sealed record ServerRebootScheduleDto
{
    public int ServerId { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public bool Enabled { get; init; }

    /// <summary>Hour and minute of the daily reboot, in <see cref="TimeZoneId"/>.</summary>
    public int Hour { get; init; }
    public int Minute { get; init; }

    /// <summary><c>UTC</c> or an IANA zone (<c>Europe/Paris</c>).</summary>
    public string TimeZoneId { get; init; } = ServerRebootOrder.UtcTimeZone;

    /// <summary>Raised by every change: an order emitted for an older version is refused.</summary>
    public int Version { get; init; }

    public DateTime UpdatedAtUtc { get; init; }
    public string UpdatedBy { get; init; } = string.Empty;
    public DateTime? NextRunUtc { get; init; }

    public RebootScheduleOutcome LastOutcome { get; init; }
    public DateTime? LastOutcomeAtUtc { get; init; }
    public DateTime? LastOccurrenceUtc { get; init; }
    public RebootSkipReason SkipReason { get; init; }

    /// <summary>For a postponement or a busy skip: the name of the task that was running.</summary>
    public string? OutcomeDetail { get; init; }

    /// <summary>The order task of the last <see cref="RebootScheduleOutcome.Ordered"/> outcome, when there is one.</summary>
    public int? LastTaskId { get; init; }
    public TaskExecutionStatus? LastTaskStatus { get; init; }
    public DateTime? LastTaskCompletedAtUtc { get; init; }
    public string? LastTaskFailureReason { get; init; }

    /// <summary>True when the caller administers the server (may change or remove the schedule).</summary>
    public bool CanManage { get; init; }

    /// <summary>True when the host announces the reboot capability (the <c>aetheus-reboot</c> grant).</summary>
    public bool AgentCanReboot { get; init; }
}

/// <summary>Creates or changes a server's daily reboot schedule.</summary>
public sealed record SaveServerRebootScheduleRequest
{
    [Range(0, 23)]
    public int Hour { get; init; }

    [Range(0, 59)]
    public int Minute { get; init; }

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string TimeZoneId { get; init; } = ServerRebootOrder.UtcTimeZone;

    public bool Enabled { get; init; } = true;
}
