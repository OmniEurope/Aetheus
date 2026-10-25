// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

/// <summary>What the backend can see of the one server a held task is pinned to, read when the task enters
/// its hold. The agent reports no slot count (MaxConcurrentTasks is local configuration), so the busy
/// state is the Assigned and Running tasks the backend itself recorded for that server.</summary>
public sealed record PendingHoldServerFacts(
    int ServerId,
    string Name,
    ServerStatus Status,
    DateTime? DeletedAt,
    DateTime LastHeartbeat,
    DateTime? LeaseExpiresAt,
    string? SessionId,
    string? AgentVersion,
    int? ProtocolVersion,
    string? CapabilitiesJson,
    bool UpdateReserved,
    DateTime? UpdateReservedAt,
    IReadOnlyList<PendingHoldActiveTask> ActiveTasks,
    int PendingTaskCount);

/// <summary>A task the server holds Assigned or Running; <c>Since</c> is when it started (or was claimed).</summary>
public sealed record PendingHoldActiveTask(int Id, TaskExecutionStatus Status, OperationKind Operation, DateTime Since);

/// <summary>The single reason, among the ones the backend can tell apart, why the held task is not claimed.</summary>
public enum PendingHoldCause
{
    /// <summary>The server row cannot be read.</summary>
    ServerUnknown,
    /// <summary>The server was retired (its agent tokens are revoked).</summary>
    ServerRetired,
    /// <summary>An administrator disabled the server.</summary>
    ServerDisabled,
    /// <summary>No heartbeat for longer than the heartbeat timeout: the agent or its host is gone.</summary>
    ServerOffline,
    /// <summary>Online, but the agent never renewed its polling lease.</summary>
    PollingNeverStarted,
    /// <summary>Online, but the polling lease lapsed: the agent sends no claim request.</summary>
    PollingLeaseLapsed,
    /// <summary>The lease is live again by the time of the diagnosis: the agent polls, so a blocker below applies.</summary>
    PollingResumed
}

public sealed record PendingHoldDiagnosis(PendingHoldCause Cause, string Summary);

/// <summary>
/// Explains a task put on hold by <c>TaskTimeoutService</c>. A task is pinned to exactly one server when it is
/// dispatched and the claim filters on that server alone, so the "eligible agents" of a held task are that
/// server's agent; the hold is entered only when its polling lease is not live. Pure function over the facts,
/// so the reasoning is testable without a database.
/// </summary>
public static class PendingHoldDiagnostics
{
    private const int MaximumListedTasks = 5;

    public static PendingHoldDiagnosis Diagnose(
        ServerTask task,
        PendingHoldServerFacts? server,
        DateTime now,
        TimeSpan offlineAgentGrace,
        TimeSpan heartbeatTimeout,
        TimeSpan leaseDuration)
    {
        ArgumentNullException.ThrowIfNull(task);

        var age = now - task.CreatedAt;
        var taskPart = string.Create(
            CultureInfo.InvariantCulture,
            $"taskAge={Span(age)} grace={Span(offlineAgentGrace)} graceLeft={Span(offlineAgentGrace - age)} pinnedServer=#{task.ServerId}");

        if (server is null)
            return new(PendingHoldCause.ServerUnknown, $"server row not found; {taskPart}");

        var heartbeatAge = now - server.LastHeartbeat;
        var cause = Classify(server, now);
        var parts = new List<string>
        {
            $"server='{server.Name}' status={server.Status}",
            $"heartbeat={Span(heartbeatAge)} ago ({(heartbeatAge <= heartbeatTimeout ? "fresh" : "stale")})",
            $"pollingLease={Lease(server.LeaseExpiresAt, now, leaseDuration)}",
            $"agent session={Short(server.SessionId)} version={server.AgentVersion ?? "unknown"} protocol={server.ProtocolVersion?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}",
            $"blockers={Blockers(task, server)}",
            $"serverActive={ActiveTasks(server, now)}",
            $"serverPending={server.PendingTaskCount.ToString(CultureInfo.InvariantCulture)}",
            "eligibleAgents=1 (claim filters on the pinned server only; role, os and pool were resolved at dispatch)",
            taskPart
        };
        return new(cause, string.Join("; ", parts));
    }

    private static PendingHoldCause Classify(PendingHoldServerFacts server, DateTime now)
    {
        if (server.DeletedAt is not null) return PendingHoldCause.ServerRetired;
        if (server.Status == ServerStatus.Disabled) return PendingHoldCause.ServerDisabled;
        if (server.Status == ServerStatus.Offline) return PendingHoldCause.ServerOffline;
        if (server.LeaseExpiresAt is null) return PendingHoldCause.PollingNeverStarted;
        return server.LeaseExpiresAt > now ? PendingHoldCause.PollingResumed : PendingHoldCause.PollingLeaseLapsed;
    }

    // The claim's own refusals (TaskRepository.ClaimPendingTasksAsync), evaluated for this task. They leave a task
    // Pending while the agent polls, so they matter once the lease is live again.
    private static string Blockers(ServerTask task, PendingHoldServerFacts server)
    {
        var blockers = new List<string>();
        if (task.Operation != OperationKind.AgentSelfUpdate)
        {
            if (server.UpdateReserved
                && !(server.UpdateReservedAt is { } reservedAt && task.CreatedAt <= reservedAt))
                blockers.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"agent-update-reserved since {server.UpdateReservedAt:u} (tasks created after it are not handed out)"));

            if (server.ProtocolVersion is not { } protocol || !AgentProtocol.IsSupported(protocol))
                blockers.Add("agent protocol unsupported or unreported");
            else if (AgentCapabilities.RequiredFor(task.Operation) is { } capability
                && !AgentCapabilities.IsDeclared(server.CapabilitiesJson, capability))
                blockers.Add($"capability '{capability}' not declared by the agent");
        }

        var selfUpdate = server.ActiveTasks.FirstOrDefault(active => active.Operation == OperationKind.AgentSelfUpdate);
        if (selfUpdate is not null && task.Operation != OperationKind.AgentSelfUpdate)
            blockers.Add($"agent self-update #{selfUpdate.Id} {selfUpdate.Status} (nothing else is handed out meanwhile)");

        return blockers.Count == 0 ? "none" : string.Join(" | ", blockers);
    }

    private static string ActiveTasks(PendingHoldServerFacts server, DateTime now)
    {
        if (server.ActiveTasks.Count == 0)
            return "0 (MaxConcurrentTasks is not reported by the agent)";

        var listed = server.ActiveTasks
            .Take(MaximumListedTasks)
            .Select(active => string.Create(
                CultureInfo.InvariantCulture,
                $"#{active.Id} {active.Status} {active.Operation} for {Span(now - active.Since)}"));
        var more = server.ActiveTasks.Count > MaximumListedTasks
            ? string.Create(CultureInfo.InvariantCulture, $" +{server.ActiveTasks.Count - MaximumListedTasks} more")
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{server.ActiveTasks.Count} [{string.Join(", ", listed)}{more}] (MaxConcurrentTasks is not reported by the agent)");
    }

    private static string Lease(DateTime? expiresAt, DateTime now, TimeSpan leaseDuration)
    {
        if (expiresAt is not { } expiry) return "never acquired";
        if (expiry > now) return $"live for another {Span(expiry - now)}";
        return $"lapsed {Span(now - expiry)} ago (last claim request about {Span(now - (expiry - leaseDuration))} ago)";
    }

    private static string Short(string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? "none" : sessionId[..Math.Min(8, sessionId.Length)];

    private static string Span(TimeSpan span)
    {
        var negative = span < TimeSpan.Zero;
        var absolute = negative ? span.Negate() : span;
        var text = absolute.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)absolute.TotalHours}h{absolute.Minutes:00}m")
            : absolute.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)absolute.TotalMinutes}m{absolute.Seconds:00}s")
                : string.Create(CultureInfo.InvariantCulture, $"{(int)absolute.TotalSeconds}s");
        return negative ? "-" + text : text;
    }
}
