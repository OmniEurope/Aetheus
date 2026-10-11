// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

public class TaskRepository(AppDbContext db, TimeProvider timeProvider) : ITaskRepository
{
    private readonly TaskLeaseRepository _leases = new(db, timeProvider);

    public Task<(List<ServerTask> Items, int TotalCount)> GetTasksPagedAsync(
        string? search, int page, int pageSize, TaskExecutionStatus? status = null, CancellationToken ct = default)
        => GetTasksPagedAsync(search, page, pageSize, status, null, null, ct);

    public async Task<(List<ServerTask> Items, int TotalCount)> GetTasksPagedAsync(
        string? search, int page, int pageSize, TaskExecutionStatus? status = null, List<int>? accessibleServerIds = null, int? serverId = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = true, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = ScopedTasks(accessibleServerIds, serverId);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => t.Name.Contains(search));

        if (status.HasValue)
            query = query.Where(t => t.Status == status.Value);

        // Recette R-212: the header filters, after the scope and before the count.
        query = TaskListQuery.Columns.ApplyFilters(query, columnFilters);

        return await PageTasksAsync(query, page, pageSize, ct, sortBy, sortDescending).ConfigureAwait(false);
    }

    public Task<TaskFilterValuesDto> GetTaskFilterValuesAsync(List<int>? accessibleServerIds, int? serverId, CancellationToken ct = default)
        => TaskListQuery.FilterValuesAsync(ScopedTasks(accessibleServerIds, serverId), ct);

    private IQueryable<ServerTask> ScopedTasks(List<int>? accessibleServerIds, int? serverId)
    {
        var query = db.Tasks.AsNoTracking().AsQueryable();

        if (accessibleServerIds is not null)
            query = query.Where(t => accessibleServerIds.Contains(t.ServerId));

        if (serverId.HasValue)
            query = query.Where(t => t.ServerId == serverId.Value);

        return query;
    }

    public async Task<(List<ServerTask> Items, int TotalCount)> GetTasksByStatusesPagedAsync(
        string? search, int page, int pageSize, List<TaskExecutionStatus> statuses, List<int>? accessibleServerIds = null, CancellationToken ct = default)
    {
        var query = db.Tasks.AsNoTracking().AsQueryable();

        if (accessibleServerIds is not null)
            query = query.Where(t => accessibleServerIds.Contains(t.ServerId));

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => t.Name.Contains(search));

        if (statuses.Count > 0)
            query = query.Where(t => statuses.Contains(t.Status));

        return await PageTasksAsync(query, page, pageSize, ct).ConfigureAwait(false);
    }

    private static async Task<(List<ServerTask> Items, int TotalCount)> PageTasksAsync(
        IQueryable<ServerTask> query,
        int page,
        int pageSize,
        CancellationToken ct,
        string? sortBy = null,
        bool sortDescending = true)
    {
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .Include(t => t.Server)
            .OrderByProperty(sortBy, sortDescending, t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    public async Task<ServerTask?> GetTaskWithServerAsync(int id, CancellationToken ct = default)
    {
        return await db.Tasks.AsNoTracking().Include(t => t.Server)
            .FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<ServerTask?> FindTaskAsync(int id, CancellationToken ct = default)
    {
        return await db.Tasks.FindAsync([id], ct).ConfigureAwait(false);
    }

    public Task<string?> GetServerAgentVersionAsync(int serverId, CancellationToken ct = default) =>
        db.Servers.AsNoTracking()
            .Where(server => server.Id == serverId)
            .Select(server => server.AgentVersion)
            .SingleOrDefaultAsync(ct);

    public async Task<ServerStatus?> GetServerStatusAsync(int serverId, CancellationToken ct = default)
    {
        return await db.Servers.AsNoTracking()
            .Where(s => s.Id == serverId)
            .Select(s => (ServerStatus?)s.Status)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> IsServerTaskPollingActiveAsync(int serverId, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await db.Servers.AsNoTracking()
            .AnyAsync(
                server => server.Id == serverId
                    && server.Status == ServerStatus.Online
                    && server.AgentSessionLeaseExpiresAt > now,
                ct)
            .ConfigureAwait(false);
    }

    public async Task<Dictionary<int, int>> GetServerIdsForTasksAsync(IReadOnlyCollection<int> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return [];
        return await db.Tasks.AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.ServerId })
            .ToDictionaryAsync(t => t.Id, t => t.ServerId, ct).ConfigureAwait(false);
    }

    public async Task<List<ServerTask>> GetPendingTasksAsync(int serverId, CancellationToken ct = default)
    {
        return await db.Tasks
            .Where(t => t.ServerId == serverId
                && t.Status == TaskExecutionStatus.Pending
                && (!t.Server.AgentUpdateReserved
                    || t.Operation == OperationKind.AgentSelfUpdate
                    || (t.Server.AgentUpdateReservedAt != null
                        && t.CreatedAt <= t.Server.AgentUpdateReservedAt)))
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ServerTask>> ClaimPendingTasksAsync(
        int serverId,
        int? take = null,
        string? agentSessionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentSessionId))
            return [];

        var fencingToken = await _leases.AcquireAgentSessionLeaseAsync(serverId, agentSessionId, ct).ConfigureAwait(false);
        if (!fencingToken.HasValue)
            return [];

        // Recette R1-42: while the agent replaces itself nothing else is handed to it. The swap ends
        // with the process exiting, so a task claimed meanwhile is cancelled without a completion and
        // later reported as a timeout. The other work stays Pending for the new agent.
        var selfUpdateInFlight = await db.Tasks.AnyAsync(t => t.ServerId == serverId
            && t.Operation == OperationKind.AgentSelfUpdate
            && (t.Status == TaskExecutionStatus.Assigned || t.Status == TaskExecutionStatus.Running), ct)
            .ConfigureAwait(false);
        if (selfUpdateInFlight)
            return [];

        // Claim Pending work only. An Assigned task may already be executing locally even if its
        // StartTask RPC has not reached the backend yet; redispatching it risks duplicate side effects.
        // TaskTimeoutService handles a claim that genuinely never starts.
        var candidates = await db.Tasks
            .Where(t => t.ServerId == serverId && t.Status == TaskExecutionStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Operation, t.CreatedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        var serverContract = await db.Servers.AsNoTracking()
            .Where(server => server.Id == serverId)
            .Select(server => new
            {
                server.AgentProtocolVersion,
                server.AgentCapabilitiesJson,
                server.AgentVersion,
                server.ScannerCapabilitiesJson,
                server.AgentUpdateReserved,
                server.AgentUpdateReservedAt
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (serverContract is null)
            return [];

        var protocolSupported = serverContract.AgentProtocolVersion is { } protocol
            && AgentProtocol.IsSupported(protocol);
        var declaredCapabilities = serverContract.AgentCapabilitiesJson ?? "[]";
        var scannerManifestSha256 = ExtractScannerManifestSha256(serverContract.ScannerCapabilitiesJson);
        candidates = candidates
            .Where(candidate =>
                candidate.Operation == OperationKind.AgentSelfUpdate
                || (!serverContract.AgentUpdateReserved
                    || (serverContract.AgentUpdateReservedAt is { } reservedAt
                        && candidate.CreatedAt <= reservedAt))
                    && (protocolSupported
                    && AgentCapabilities.RequiredFor(candidate.Operation) is { } capability
                    && declaredCapabilities.Contains($"\"{capability}\"", StringComparison.Ordinal)))
            .ToList();

        // A self-update is exclusive and takes priority over normal work. Claiming it alongside an
        // older unsupported task lets that task reach the agent first and can wedge the very poller
        // needed to install the compatible binary. One update per poll also prevents duplicate swaps.
        var selfUpdate = candidates.FirstOrDefault(c => c.Operation == OperationKind.AgentSelfUpdate);
        var claimable = selfUpdate is not null
            ? candidates.Where(c => c.Id == selfUpdate.Id)
            : take is { } t
                ? candidates.Take(Math.Clamp(t, 0, 50))
                : candidates;
        var candidateIds = claimable.Select(c => c.Id).Distinct().ToList();

        if (candidateIds.Count == 0)
            return [];

        if (db.Database.IsRelational())
        {
            var assignedAt = timeProvider.GetUtcNow().UtcDateTime;
            // Batch atomic compare-and-set: claim all candidates in a single round-trip.
            var updatedRows = await db.Tasks
                .Where(t => candidateIds.Contains(t.Id)
                    && t.Status == TaskExecutionStatus.Pending
                    && t.Server.AgentSessionId == agentSessionId
                    && t.Server.AgentSessionFencingToken == fencingToken.Value
                    && t.Server.AgentSessionLeaseExpiresAt > assignedAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.Status, TaskExecutionStatus.Assigned)
                    .SetProperty(t => t.AssignedAt, assignedAt)
                    .SetProperty(t => t.AssignedAgentSessionId, agentSessionId)
                    .SetProperty(t => t.AssignedAgentSessionFencingToken, fencingToken.Value)
                    .SetProperty(t => t.AssignedAgentVersion, serverContract.AgentVersion)
                    .SetProperty(t => t.AssignedScannerManifestSha256, scannerManifestSha256), ct)
                .ConfigureAwait(false);

            if (updatedRows == 0)
                return [];
        }
        else
        {
            // InMemory (unit tests) does not support ExecuteUpdate - load + set instead.
            var tasks = await db.Tasks
                .Where(t => candidateIds.Contains(t.Id) && t.Status == TaskExecutionStatus.Pending)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var task in tasks)
            {
                task.Status = TaskExecutionStatus.Assigned;
                task.AssignedAt = timeProvider.GetUtcNow().UtcDateTime;
                task.AssignedAgentSessionId = agentSessionId;
                task.AssignedAgentSessionFencingToken = fencingToken.Value;
                task.AssignedAgentVersion = serverContract.AgentVersion;
                task.AssignedScannerManifestSha256 = scannerManifestSha256;
            }
            if (tasks.Count > 0)
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (tasks.Count == 0)
                return [];
        }

        return await db.Tasks
            .Where(t => candidateIds.Contains(t.Id)
                && t.Status == TaskExecutionStatus.Assigned
                && t.AssignedAgentSessionId == agentSessionId
                && t.AssignedAgentSessionFencingToken == fencingToken.Value
                && t.Server.AgentSessionId == agentSessionId
                && t.Server.AgentSessionFencingToken == fencingToken.Value)
            .Include(t => t.PipelineStepRun)
            .Include(t => t.PipelineRun)
                .ThenInclude(run => run!.Pipeline)
                    .ThenInclude(pipeline => pipeline.Project)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    internal static string? ExtractScannerManifestSha256(string? capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson)) return null;
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(capabilitiesJson) ?? [];
            const string prefix = "scanner-manifest:sha256:";
            var value = values.FirstOrDefault(item => item.StartsWith(prefix, StringComparison.Ordinal));
            var hash = value?[prefix.Length..];
            return hash is { Length: 64 } && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<bool> HasCurrentAgentLeaseAsync(
        int taskId,
        int serverId,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default) =>
        _leases.HasCurrentAgentLeaseAsync(taskId, serverId, agentSessionId, fencingToken, ct);

    public Task<bool> HaveCurrentAgentLeasesAsync(
        IReadOnlyCollection<int> taskIds,
        int serverId,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default) =>
        _leases.HaveCurrentAgentLeasesAsync(taskIds, serverId, agentSessionId, fencingToken, ct);

    public Task<bool> TryStartTaskWithLeaseAsync(
        ServerTask task,
        string agentSessionId,
        long fencingToken,
        DateTime startedAt,
        CancellationToken ct = default) =>
        _leases.TryStartTaskWithLeaseAsync(task, agentSessionId, fencingToken, startedAt, ct);

    public Task<bool> TryReleaseAssignedTaskWithLeaseAsync(
        ServerTask task,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default) =>
        _leases.TryReleaseAssignedTaskWithLeaseAsync(task, agentSessionId, fencingToken, ct);

    public Task<bool> TryStartTaskAsync(
        ServerTask task,
        DateTime startedAt,
        CancellationToken ct = default) =>
        _leases.TryStartTaskAsync(task, startedAt, ct);

    public Task<bool> TryCompleteTaskWithLeaseAsync(
        ServerTask task,
        string agentSessionId,
        long fencingToken,
        TaskExecutionStatus status,
        int exitCode,
        DateTime completedAt,
        string environmentVariables,
        CancellationToken ct = default) =>
        _leases.TryCompleteTaskWithLeaseAsync(
            task, agentSessionId, fencingToken, status, exitCode, completedAt, environmentVariables, ct);

    public async Task<ServerTask> AddTaskAsync(ServerTask task, CancellationToken ct = default)
    {
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await db.Tasks.Include(t => t.Server)
            .FirstAsync(t => t.Id == task.Id, ct).ConfigureAwait(false);
    }

    public async Task<PipelineStepRun?> FindPipelineStepRunAsync(int id, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<int, PipelineStepRun>> FindPipelineStepRunsByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        return await db.PipelineStepRuns
            .Where(r => ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct).ConfigureAwait(false);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ServerTask>> GetStaleRunningTasksAsync(TimeSpan threshold, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var fallbackCutoff = now - threshold;

        // Keep timeout evaluation in SQL and cap every watchdog sweep. Explicit per-task budgets
        // remain authoritative; only legacy rows without a positive timeout use the global fallback.
        return await db.Tasks
            .Where(task => task.Status == TaskExecutionStatus.Running
                && task.StartedAt != null
                && ((task.TimeoutSeconds > 0
                        && task.StartedAt.Value.AddSeconds(task.TimeoutSeconds) < now)
                    || (task.TimeoutSeconds <= 0 && task.StartedAt.Value < fallbackCutoff)))
            .OrderBy(task => task.StartedAt)
            .ThenBy(task => task.Id)
            .Take(1000)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ServerTask>> GetStaleAssignedTasksAsync(TimeSpan threshold, CancellationToken ct = default)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - threshold;
        return await db.Tasks
            .Where(t => t.Status == TaskExecutionStatus.Assigned && t.StartedAt == null
                && (t.AssignedAt ?? t.CreatedAt) < cutoff)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ServerTask>> GetStalePendingTasksAsync(TimeSpan threshold, CancellationToken ct = default)
    {
        // Heartbeat liveness and task-poller liveness are independent. A runner that still sends
        // monitoring heartbeats but no longer calls /tasks/claim must not keep its queue forever.
        // AcquireAgentSessionLeaseAsync renews this dedicated polling lease on every claim request,
        // including empty polls, so an active busy runner retains its queued work.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now - threshold;
        // PLAN-004 R-11: a retired server (Offline, tokens revoked) must still age out the work queued
        // for it, or its pipeline runs would wait forever on a filtered-out server.
        var servers = db.Servers.IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired]);
        return await db.Tasks
            .Where(t => t.Status == TaskExecutionStatus.Pending
                && !t.IsDeferredCleanup
                && t.CreatedAt < cutoff
                && servers.Any(server =>
                    server.Id == t.ServerId
                    && (server.Status != ServerStatus.Online
                        || server.AgentSessionLeaseExpiresAt == null
                        || server.AgentSessionLeaseExpiresAt <= now)))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ServerTask>> GetTasksFromSupersededAgentSessionsAsync(CancellationToken ct = default)
    {
        return await db.Tasks
            .Where(task =>
                (task.Status == TaskExecutionStatus.Assigned || task.Status == TaskExecutionStatus.Running)
                // Replacing the agent session is the expected completion path for self-update.
                // AgentUpdateRepository owns that task until the new heartbeat confirms the
                // requested release or its dedicated confirmation deadline expires.
                && task.Operation != OperationKind.AgentSelfUpdate
                && task.AssignedAgentSessionId != null
                // Supersession is a REPLACED session, nothing else - that is the only conclusive
                // evidence the claiming process is gone. A lease that merely lapsed while its own
                // session is still the one on the server proves nothing: the agent may be mid-build
                // with its poll loop starved, and parking its running task there is what killed
                // BuildPayloads on a runner shared by two pipelines. An agent that lapses and never
                // returns is still settled, by the Running/Assigned timeouts and the offline parking.
                && (task.Server.AgentSessionId == null
                    || task.AssignedAgentSessionId != task.Server.AgentSessionId
                    || task.AssignedAgentSessionFencingToken != task.Server.AgentSessionFencingToken))
            .OrderBy(task => task.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> DeleteCompletedTasksOlderThanAsync(
        DateTime taskCutoff,
        DateTime pipelineRunCutoff,
        CancellationToken ct = default)
    {
        // Standalone terminal tasks follow TaskDays directly. Pipeline tasks are deleted only when
        // both their own completion and the parent run are outside their respective retention
        // windows. This bounds the Tasks table even when an expired run remains temporarily
        // present (for example while deferred cleanup is pending), without blanking a retained run.
        // Set-based on relational (the table grows forever otherwise); InMemory fallback for tests.
        var terminalStatuses = new[]
        {
            TaskExecutionStatus.Success, TaskExecutionStatus.Failed,
            TaskExecutionStatus.Cancelled, TaskExecutionStatus.Timeout
        };

        if (db.Database.IsRelational())
        {
            return await db.Tasks
                .Where(t => terminalStatuses.Contains(t.Status)
                    && t.CompletedAt != null
                    && t.CompletedAt < taskCutoff
                    && (t.PipelineRunId == null || t.PipelineRun!.StartedAt < pipelineRunCutoff))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        var old = await db.Tasks
            .Where(t => terminalStatuses.Contains(t.Status)
                && t.CompletedAt != null
                && t.CompletedAt < taskCutoff
                && (t.PipelineRunId == null || t.PipelineRun!.StartedAt < pipelineRunCutoff))
            .ToListAsync(ct).ConfigureAwait(false);
        db.Tasks.RemoveRange(old);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return old.Count;
    }

    public async Task<Dictionary<int, string>> GetTaskStatusesAsync(List<int> taskIds, int serverId, CancellationToken ct = default)
    {
        return await db.Tasks.AsNoTracking()
            .Where(t => taskIds.Contains(t.Id) && t.ServerId == serverId)
            .Select(t => new { t.Id, t.Status })
            .ToDictionaryAsync(t => t.Id, t => t.Status.ToString(), ct).ConfigureAwait(false);
    }
}
