// SPDX-License-Identifier: EUPL-1.2
using System.Data;
using System.Text.Json;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AgentUpdate;

internal sealed class AgentUpdateRepository(AppDbContext db, TimeProvider timeProvider)
    : IAgentUpdateRepository
{
    private const int UpdateTimeoutSeconds = 600;
    // Reads the server data owned by agent-update workflows locally to avoid a dependency cycle
    // with Servers (ADR-039).

    /// <summary>
    /// Servers the agent-update view lists, optionally narrowed to what the caller may see.
    /// Moved out of ServerRepository, which had no other consumer for it.
    /// </summary>
    public async Task<List<Server>> GetServersForAgentUpdateAsync(
        List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = db.Servers.AsQueryable();
        if (accessibleIds is not null)
            query = query.Where(server => accessibleIds.Contains(server.Id));

        return await query
            .OrderBy(server => server.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Tracked single-server load for the update flow. A local variant of the shared
    /// <c>ServerRepository.FindServerAsync</c>, not a move: that one has many other callers, and
    /// relocating it would have traded one cross-module dependency for several.
    /// </summary>
    public async Task<Server?> FindServerAsync(int id, CancellationToken ct = default) =>
        await db.Servers.FindAsync([id], ct).ConfigureAwait(false);

    /// <summary>Uses the shared <see cref="ServerCompatibilityFacts"/> query projection for
    /// consistent compatibility verdicts without a dependency on the Servers repository.</summary>
    public Task<List<ServerDto>> GetServersForCompatibilityAsync(
        List<int>? accessibleIds, CancellationToken ct = default) =>
        ServerCompatibilityFacts.ReadAsync(db.Servers, accessibleIds, ct);

    /// <summary>
    /// Work in flight on a server that is not itself an agent update - what makes it too busy to be
    /// updated now. A local variant for the same reason as the compatibility read above.
    /// </summary>
    public Task<int> CountActiveNonUpdateTasksAsync(int serverId, CancellationToken ct = default) =>
        db.Tasks.CountAsync(task =>
            task.ServerId == serverId
            && task.Operation != OperationKind.AgentSelfUpdate
            && (task.Status == TaskExecutionStatus.Pending
                || task.Status == TaskExecutionStatus.Assigned
                || task.Status == TaskExecutionStatus.Running), ct);

    /// <summary>Counts active tasks for the fleet in one query. Servers without active tasks are
    /// omitted from the result.</summary>
    public async Task<IReadOnlyDictionary<int, int>> CountActiveNonUpdateTasksAsync(
        IReadOnlyCollection<int> serverIds, CancellationToken ct = default)
    {
        if (serverIds.Count == 0) return new Dictionary<int, int>();

        var counts = await db.Tasks
            .Where(task => serverIds.Contains(task.ServerId)
                && task.Operation != OperationKind.AgentSelfUpdate
                && (task.Status == TaskExecutionStatus.Pending
                    || task.Status == TaskExecutionStatus.Assigned
                    || task.Status == TaskExecutionStatus.Running))
            .GroupBy(task => task.ServerId)
            .Select(group => new { ServerId = group.Key, Count = group.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        return counts.ToDictionary(entry => entry.ServerId, entry => entry.Count);
    }

    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMinutes(5);

    public async Task<(AgentUpdateRequest Request, bool Created)> ReserveAsync(
        Server server,
        AgentReleaseManifestDto release,
        string requestedBy,
        CancellationToken ct,
        IReadOnlyList<string>? enableCapabilities = null)
    {
        // Callers hand over tokens already validated against AgentEnableableCapabilities.Whitelist.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false)
            : null;

        // Serialize reservations by updating the durable server row inside the transaction.
        // The self-assignment is intentionally value-neutral but still acquires the provider row
        // lock. Read Committed is sufficient because every contender takes this lock before reading
        // active requests; Serializable would turn the expected contention into SQLSTATE 40001.
        var reservedServer = server;
        if (db.Database.IsRelational())
        {
            await db.Servers
                .Where(item => item.Id == server.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    item => item.AgentUpdateReserved,
                    item => item.AgentUpdateReserved), ct)
                .ConfigureAwait(false);
        }

        var existing = await db.AgentUpdateRequests
            .FirstOrDefaultAsync(request =>
                request.ServerId == server.Id
                && request.TargetVersion == release.SoftwareVersion
                && request.IsActive, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            // A request that has not produced its task yet can still take the capabilities asked for
            // now. Once the task exists its environment is fixed: the caller sees that the request does
            // not carry them and refuses instead of reporting a grant that will not happen.
            if (existing.TaskId is null && enableCapabilities is { Count: > 0 })
            {
                MergeCapabilities(existing, enableCapabilities);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            if (transaction is not null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (existing, false);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var request = new AgentUpdateRequest
        {
            ServerId = server.Id,
            TargetVersion = release.SoftwareVersion,
            ObservedVersion = reservedServer.AgentVersion,
            ObservedProtocolVersion = reservedServer.AgentProtocolVersion,
            ObservedSessionId = reservedServer.AgentSessionId,
            RequestedBy = requestedBy,
            RequestedAt = now,
            Status = AgentUpdateRequestStatus.WaitingForIdle,
            IsActive = true,
            ExpectedCapabilitiesJson = JsonSerializer.Serialize(
                BuildExpectedCapabilities(reservedServer))
        };
        if (enableCapabilities is { Count: > 0 })
            MergeCapabilities(request, enableCapabilities);
        reservedServer.AgentUpdateReserved = true;
        reservedServer.AgentUpdateReservedAt = now;
        db.AgentUpdateRequests.Add(request);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        return (request, true);
    }

    public async Task<(AgentUpdateRequest Request, ServerTask? CreatedTask, int BlockingTaskCount)> TryQueueAsync(
        int requestId,
        CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false)
            : null;
        var request = await db.AgentUpdateRequests
            .Include(item => item.Server)
            .FirstAsync(item => item.Id == requestId, ct)
            .ConfigureAwait(false);
        if (!request.IsActive || request.TaskId is not null)
        {
            if (transaction is not null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (request, null, 0);
        }

        // Pending work has not started and is already fenced behind the update reservation. Counting
        // it as active creates a circular wait when the old agent cannot claim that exact operation:
        // the task waits for a newer capability while the self-update waits for the task. Queue the
        // exclusive, priority self-update ahead of pending work; only an assigned/running operation
        // must finish before the agent process can safely restart.
        var blocking = await db.Tasks.CountAsync(task =>
            task.ServerId == request.ServerId
            && task.Operation != OperationKind.AgentSelfUpdate
            && (request.Server.AgentUpdateReservedAt == null
                || task.CreatedAt <= request.Server.AgentUpdateReservedAt)
            && (task.Status == TaskExecutionStatus.Assigned
                || task.Status == TaskExecutionStatus.Running), ct).ConfigureAwait(false);
        if (blocking > 0)
        {
            if (transaction is not null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (request, null, blocking);
        }

        var task = new ServerTask
        {
            ServerId = request.ServerId,
            Name = $"Agent self-update to {request.TargetVersion}",
            Command = string.Empty,
            Executor = ExecutorType.Operation,
            Operation = OperationKind.AgentSelfUpdate,
            Status = TaskExecutionStatus.Pending,
            TimeoutSeconds = UpdateTimeoutSeconds,
            EnvironmentVariables = JsonSerializer.Serialize(BuildTaskEnvironment(request)),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        request.TaskId = task.Id;
        request.Status = AgentUpdateRequestStatus.Queued;
        request.StartedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        return (request, task, 0);
    }

    public Task<List<int>> GetWaitingRequestIdsAsync(CancellationToken ct) =>
        db.AgentUpdateRequests.AsNoTracking()
            .Where(request => request.IsActive
                && request.Status == AgentUpdateRequestStatus.WaitingForIdle)
            .OrderBy(request => request.RequestedAt)
            .Select(request => request.Id)
            .ToListAsync(ct);

    public Task<List<PendingTaskAgentContext>> GetPendingTasksOfPollingAgentsAsync(
        TimeSpan olderThan, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var createdBefore = now - olderThan;
        // Online with a live polling lease: the declared capabilities then describe an agent that is
        // asking for work right now, so a task it never receives is not waiting for it to come back.
        // A reserved server is mid-update: its fenced work resumes with the new agent, so it is left
        // alone. The self-update itself is claimed regardless of capabilities.
        return db.Tasks.AsNoTracking()
            .Where(task => task.Status == TaskExecutionStatus.Pending
                && !task.IsDeferredCleanup
                && task.Operation != OperationKind.AgentSelfUpdate
                && task.CreatedAt < createdBefore
                && task.Server.Status == ServerStatus.Online
                && !task.Server.AgentUpdateReserved
                && task.Server.AgentSessionLeaseExpiresAt > now)
            .OrderBy(task => task.CreatedAt)
            .Take(200)
            .Select(task => new PendingTaskAgentContext(
                task.Id,
                task.Operation,
                task.Server.Name,
                task.Server.AgentVersion,
                task.Server.AgentProtocolVersion,
                task.Server.AgentCapabilitiesJson))
            .ToListAsync(ct);
    }

    public Task<AgentUpdateRequest?> FindActiveByServerAsync(int serverId, CancellationToken ct) =>
        db.AgentUpdateRequests.AsNoTracking()
            .Where(request => request.ServerId == serverId && request.IsActive)
            .OrderByDescending(request => request.RequestedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<AgentUpdateRequest?> MarkProgressAsync(
        int serverId,
        AgentUpdateProgressDto progress,
        CancellationToken ct)
    {
        var request = await db.AgentUpdateRequests
            .FirstOrDefaultAsync(item => item.ServerId == serverId && item.IsActive, ct)
            .ConfigureAwait(false);
        if (request is null) return null;

        request.Status = progress.Phase switch
        {
            AgentUpdatePhase.Downloading => AgentUpdateRequestStatus.Downloading,
            AgentUpdatePhase.Downloaded or AgentUpdatePhase.Extracting
                => AgentUpdateRequestStatus.Staging,
            AgentUpdatePhase.LaunchingUpdater or AgentUpdatePhase.AgentOffline or AgentUpdatePhase.Done
                => AgentUpdateRequestStatus.Handoff,
            AgentUpdatePhase.Failed => AgentUpdateRequestStatus.Failed,
            _ => request.Status
        };
        if (request.Status == AgentUpdateRequestStatus.Handoff && request.HandoffAt is null)
        {
            request.HandoffAt = timeProvider.GetUtcNow().UtcDateTime;
            request.ConfirmationDeadline = request.HandoffAt.Value.Add(ConfirmationTimeout);
        }
        if (request.Status == AgentUpdateRequestStatus.Failed)
        {
            request.IsActive = false;
            request.FailureCode = "agent-update-failed";
            request.FailureDiagnostic = progress.Message;
            var server = await db.Servers.FirstAsync(item => item.Id == serverId, ct).ConfigureAwait(false);
            server.AgentUpdateReserved = false;
            server.AgentUpdateReservedAt = null;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return request;
    }

    public async Task<AgentUpdateRequest?> ConfirmFromHeartbeatAsync(
        int serverId,
        ServerHeartbeatDto heartbeat,
        CancellationToken ct)
    {
        var request = await db.AgentUpdateRequests
            .Include(item => item.Server)
            .Include(item => item.Task)
            .FirstOrDefaultAsync(item => item.ServerId == serverId && item.IsActive, ct)
            .ConfigureAwait(false);
        if (request is null || request.Status != AgentUpdateRequestStatus.Handoff)
            return null;

        var sessionChanged = !string.IsNullOrWhiteSpace(heartbeat.AgentSessionId)
            && !string.Equals(heartbeat.AgentSessionId, request.ObservedSessionId, StringComparison.Ordinal);
        if (!sessionChanged) return null;

        // Linux hands off to a root-owned systemd worker after the agent exits. systemd can restart
        // the old binary once before that worker swaps the installation, so keep waiting until the
        // target version confirms or the existing confirmation deadline expires.
        if (string.Equals(heartbeat.AgentVersion, request.ObservedVersion, StringComparison.Ordinal))
            return null;

        if (!string.Equals(heartbeat.AgentVersion, request.TargetVersion, StringComparison.Ordinal))
            return await FailAsync(request, "wrong-version",
                $"Expected {request.TargetVersion}, received {heartbeat.AgentVersion ?? "missing"}.", ct)
                .ConfigureAwait(false);
        if (heartbeat.AgentProtocolVersion is not { } protocol || !AgentProtocol.IsSupported(protocol))
            return await FailAsync(request, "unsupported-protocol",
                $"Received unsupported protocol {heartbeat.AgentProtocolVersion?.ToString() ?? "missing"}.", ct)
                .ConfigureAwait(false);

        var expected = JsonSerializer.Deserialize<List<string>>(request.ExpectedCapabilitiesJson) ?? [];
        var reported = heartbeat.AgentCapabilities ?? [];
        var missing = expected.Except(reported, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            return await FailAsync(request, "missing-capabilities",
                $"Missing expected capabilities: {string.Join(", ", missing)}.", ct).ConfigureAwait(false);

        request.Status = AgentUpdateRequestStatus.Confirmed;
        request.IsActive = false;
        request.ConfirmedAt = timeProvider.GetUtcNow().UtcDateTime;
        request.ConfirmedSessionId = heartbeat.AgentSessionId;
        request.Server.AgentUpdateReserved = false;
        request.Server.AgentUpdateReservedAt = null;
        CompleteUpdateTask(
            request,
            TaskExecutionStatus.Success,
            request.ConfirmedAt.Value,
            exitCode: 0);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return request;
    }

    public async Task<List<AgentUpdateRequest>> FailExpiredAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updateDeadline = now.AddSeconds(-UpdateTimeoutSeconds);
        var expired = await db.AgentUpdateRequests
            .Include(request => request.Server)
            .Include(request => request.Task)
            .Where(request => request.IsActive
                && ((request.Status == AgentUpdateRequestStatus.Handoff
                        && request.ConfirmationDeadline != null
                        && request.ConfirmationDeadline < now)
                    || (request.Status != AgentUpdateRequestStatus.WaitingForIdle
                        && request.Status != AgentUpdateRequestStatus.Handoff
                        && request.StartedAt != null
                        && request.StartedAt < updateDeadline)
                    // Once the old agent has handed off, its task can be marked terminal by a
                    // concurrent generic watchdog as the process session changes. The heartbeat
                    // contract, not that stale task state, owns the outcome during confirmation.
                    || (request.Status != AgentUpdateRequestStatus.Handoff
                        && request.Task != null
                        && (request.Task.Status == TaskExecutionStatus.Failed
                            || request.Task.Status == TaskExecutionStatus.Timeout
                            || request.Task.Status == TaskExecutionStatus.Cancelled))))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var request in expired)
        {
            request.Status = AgentUpdateRequestStatus.Failed;
            request.IsActive = false;
            var confirmationTimedOut = request.HandoffAt is not null;
            request.FailureCode = confirmationTimedOut
                ? "confirmation-timeout"
                : "update-task-timeout";
            request.FailureDiagnostic = confirmationTimedOut
                ? "No confirming heartbeat arrived before the deadline."
                : "The update task did not reach handoff before its execution deadline.";
            if (request.Task is
                {
                    Status: TaskExecutionStatus.Pending
                    or TaskExecutionStatus.Assigned
                    or TaskExecutionStatus.Running
                })
            {
                CompleteUpdateTask(
                    request,
                    confirmationTimedOut
                        ? TaskExecutionStatus.Timeout
                        : TaskExecutionStatus.Cancelled,
                    now,
                    exitCode: -1);
            }
            request.Server.AgentUpdateReserved = false;
            request.Server.AgentUpdateReservedAt = null;
        }
        if (expired.Count > 0)
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return expired;
    }

    private async Task<AgentUpdateRequest> FailAsync(
        AgentUpdateRequest request,
        string code,
        string diagnostic,
        CancellationToken ct)
    {
        request.Status = AgentUpdateRequestStatus.Failed;
        request.IsActive = false;
        request.FailureCode = code;
        request.FailureDiagnostic = diagnostic;
        request.Server.AgentUpdateReserved = false;
        request.Server.AgentUpdateReservedAt = null;
        if (request.Task is
            {
                Status: TaskExecutionStatus.Pending
                or TaskExecutionStatus.Assigned
                or TaskExecutionStatus.Running
            })
        {
            CompleteUpdateTask(
                request,
                TaskExecutionStatus.Failed,
                timeProvider.GetUtcNow().UtcDateTime,
                exitCode: -1);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return request;
    }

    private static void CompleteUpdateTask(
        AgentUpdateRequest request,
        TaskExecutionStatus status,
        DateTime completedAt,
        int exitCode)
    {
        if (request.Task is not { } task) return;
        task.Status = status;
        task.ExitCode = exitCode;
        task.CompletedAt = completedAt;
        task.EnvironmentVariables = TaskEnvProtection.EmptyEnv;
    }

    private static Dictionary<string, string> BuildTaskEnvironment(AgentUpdateRequest request)
    {
        var environment = new Dictionary<string, string>
        {
            ["AETHEUS_AGENT_TARGET_VERSION"] = request.TargetVersion
        };
        if (!string.IsNullOrEmpty(request.EnableCapabilities))
            environment[AgentEnableableCapabilities.EnvironmentVariable] = request.EnableCapabilities;
        return environment;
    }

    /// <summary>Adds the requested grants to the request and, since a grant that is installed must then
    /// show in the agent's heartbeat, to the capabilities the confirming heartbeat has to report.</summary>
    private static void MergeCapabilities(AgentUpdateRequest request, IReadOnlyList<string> requested)
    {
        // Fail closed: a stored or requested value outside the whitelist is never merged, trimmed or
        // replaced by an empty grant; the whole reservation stops so nothing is persisted.
        if (!AgentEnableableCapabilities.TryParse(request.EnableCapabilities, out var carried, out var carriedRejected))
            throw new InvalidOperationException(
                $"The stored capability grant of update request {request.Id} holds a value outside the whitelist: \"{carriedRejected}\".");
        if (!AgentEnableableCapabilities.TryNormalize([.. carried, .. requested], out var merged, out var requestedRejected))
            throw new InvalidOperationException(
                $"The requested capability \"{requestedRejected}\" is outside the whitelist and cannot be granted.");
        request.EnableCapabilities = AgentEnableableCapabilities.Join(merged);

        var expected = JsonSerializer.Deserialize<List<string>>(request.ExpectedCapabilitiesJson) ?? [];
        if (merged.Contains(AgentEnableableCapabilities.PatchManage)
            && !expected.Contains(AgentCapabilities.PatchManagement, StringComparer.Ordinal))
            expected.Add(AgentCapabilities.PatchManagement);
        request.ExpectedCapabilitiesJson = JsonSerializer.Serialize(expected.Order(StringComparer.Ordinal).ToList());
    }

    private static List<string> BuildExpectedCapabilities(Server server)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            AgentCapabilities.SelfUpdate,
            AgentCapabilities.ShellExecution
        };
        // Mirrors HeartbeatService.BuildEffectiveCapabilities: a runner-enabled agent reports
        // pipeline.build and pipeline.functions together (type: aetheus steps need the latter).
        if (server.PipelineRunnerEnabled)
        {
            expected.Add(AgentCapabilities.PipelineBuild);
            expected.Add(AgentCapabilities.PipelineFunctions);
        }
        // Same rule for deployment: deployment.apply and deployment.functions come from one grant.
        if (server.DeploymentTargetAvailable)
        {
            expected.Add(AgentCapabilities.Deployment);
            expected.Add(AgentCapabilities.DeploymentFunctions);
        }
        return expected.Order(StringComparer.Ordinal).ToList();
    }
}
