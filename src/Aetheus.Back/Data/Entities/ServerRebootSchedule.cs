// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Back.Data.Entities;

/// <summary>
/// Recette R-8 (option B of 2026-11-07): the daily reboot of a server, scheduled by Aetheus and executed by its agent
/// in place of the host's own cron. One per server. <see cref="Version"/> rises with every change, so an order emitted
/// for an older version is refused by the agent's confirmation.
/// </summary>
public class ServerRebootSchedule
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public bool Enabled { get; set; }
    public int Hour { get; set; }
    public int Minute { get; set; }

    /// <summary><c>UTC</c> or an IANA zone.</summary>
    public string TimeZoneId { get; set; } = ServerRebootOrder.UtcTimeZone;

    public int Version { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;

    /// <summary>The last occurrence the scheduler settled (ordered or skipped): never handled twice.</summary>
    public DateTime? LastHandledOccurrenceUtc { get; set; }

    public RebootScheduleOutcome LastOutcome { get; set; }
    public DateTime? LastOutcomeAtUtc { get; set; }

    /// <summary>The occurrence the last outcome is about (the postponed one while it waits).</summary>
    public DateTime? LastOccurrenceUtc { get; set; }

    public RebootSkipReason SkipReason { get; set; }

    /// <summary>The name of the task that kept the server busy, for a postponement or a busy skip.</summary>
    public string? OutcomeDetail { get; set; }

    /// <summary>The order task of the current occurrence: the only one the agent's confirmation accepts.</summary>
    public int? LastTaskId { get; set; }

    public Server Server { get; set; } = null!;
}
