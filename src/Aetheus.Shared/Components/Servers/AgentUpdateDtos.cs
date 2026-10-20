// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Servers;

/// <summary>
/// Result of queuing an agent self-update for a single server. The actual update runs
/// asynchronously on the agent; progress and the final outcome surface through the
/// existing task-log / <c>TaskCompleted</c> SignalR channel.
/// </summary>
public sealed record AgentUpdateResponse
{
    public int ServerId { get; init; }
    public int? RequestId { get; init; }
    public string SourceVersion { get; init; } = string.Empty;
    public string TargetVersion { get; init; } = string.Empty;
    /// <summary>
    /// Id of the self-update task, or 0 while existing work is still draining. The non-nullable
    /// wire shape is retained for N-1 clients that predate deferred queueing.
    /// </summary>
    public int TaskId { get; init; }
    public AgentUpdateRequestStatus Status { get; init; }
    public int BlockingTaskCount { get; init; }
    public bool ExistingRequest { get; init; }
    public AgentUpdateQueueOutcome Outcome { get; init; }
}

/// <summary>
/// Body of the "update the agent and enable capabilities" action (R3-58). Each entry must be one of
/// <see cref="AgentEnableableCapabilities.Whitelist"/>; the backend refuses anything else with a 400.
/// </summary>
public sealed record AgentEnableCapabilitiesRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(8)]
    [MaxItemStringLength(32)]
    public List<string> Capabilities { get; init; } = [];
}

/// <summary>
/// Result of queuing an agent self-update across every server the caller can administer.
/// </summary>
public sealed record AgentUpdateAllResponse
{
    /// <summary>Number of servers for which a self-update task was queued.</summary>
    public int QueuedCount { get; init; }

    /// <summary>Ids of the servers a self-update task was queued for.</summary>
    public List<int> ServerIds { get; init; } = [];
    public int AlreadyUpToDateCount { get; init; }
    public int OfflineCount { get; init; }
    public int BusyCount { get; init; }
    public List<AgentUpdateResponse> Results { get; init; } = [];
}

public sealed record AgentUpdateAllPreviewDto
{
    public string TargetVersion { get; init; } = string.Empty;
    public int AffectedCount { get; init; }
    public int AlreadyUpToDateCount { get; init; }
    public int OfflineCount { get; init; }
    public int IncompatibleCount { get; init; }
    public int BusyCount { get; init; }
}

public sealed record AgentUpdateRequestSummaryDto
{
    public int RequestId { get; init; }
    public string ObservedVersion { get; init; } = string.Empty;
    public string TargetVersion { get; init; } = string.Empty;
    public AgentUpdateRequestStatus Status { get; init; }
    public int BlockingTaskCount { get; init; }
    public DateTime RequestedAt { get; init; }
    public DateTime? HandoffAt { get; init; }
    public DateTime? ConfirmedAt { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureDiagnostic { get; init; }
}

/// <summary>
/// Agent-side progress report for the self-update operation (item #3 of the plan). Emitted at
/// each phase transition of <c>AgentSelfUpdateOperationExecutor</c>. Backend persists nothing
/// (transient) and broadcasts it as <c>AgentUpdateProgress</c> on the server hub group.
/// </summary>
public sealed record AgentUpdateProgressDto
{
    public int ServerId { get; init; }
    public AgentUpdatePhase Phase { get; init; }

    /// <summary>0..100 progress percent. The agent emits a coarse value (10/20/50/60/80) so
    /// the UI doesn't need to compute byte-level progress while extraction happens.</summary>
    [Range(0, 100)]
    public int Percent { get; init; }

    /// <summary>Optional free-form context (e.g. "Downloaded 4500 KB", "tar exit code 2: …").
    /// Bounded so a misbehaving agent cannot flood the hub channel.</summary>
    [StringLength(500)]
    public string? Message { get; init; }
}

/// <summary>
/// Request body sent by the agent when it reports a progress phase. The matching server id is
/// derived from the agent's JWT (<c>ServerId</c> claim), so the request only carries the
/// payload - no caller-supplied server id to spoof.
/// </summary>
public sealed record AgentUpdateProgressReport
{
    public AgentUpdatePhase Phase { get; init; }

    [Range(0, 100)]
    public int Percent { get; init; }

    [StringLength(500)]
    public string? Message { get; init; }
}
