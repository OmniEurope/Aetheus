// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

internal sealed class TaskLeaseRepository(AppDbContext db, TimeProvider timeProvider)
{
    internal static readonly TimeSpan AgentSessionLeaseDuration = TimeSpan.FromMinutes(2);

    // The task is assigned to this agent session and fencing token, and that session still holds a live
    // lease on the task's server: the precondition of every lease-guarded task transition.
    private static Expression<Func<ServerTask, bool>> HeldByLiveAgentSession(
        string agentSessionId, long fencingToken, DateTime now) =>
        task => task.AssignedAgentSessionId == agentSessionId
            && task.AssignedAgentSessionFencingToken == fencingToken
            && task.Server.AgentSessionId == agentSessionId
            && task.Server.AgentSessionFencingToken == fencingToken
            && task.Server.AgentSessionLeaseExpiresAt > now;

    internal async Task<long?> AcquireAgentSessionLeaseAsync(
        int serverId,
        string agentSessionId,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(AgentSessionLeaseDuration);

        if (db.Database.IsRelational())
        {
            var renewed = await db.Servers
                .Where(server => server.Id == serverId
                    && server.AgentSessionId == agentSessionId
                    && server.AgentSessionLeaseExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(server => server.AgentSessionLeaseExpiresAt, expiresAt),
                    ct)
                .ConfigureAwait(false);

            if (renewed == 0)
            {
                // The same session coming back after its lease lapsed is the SAME process, so nothing
                // needs fencing off and the token must not move. Bumping it here orphaned the agent's
                // own in-flight work: every lease check below also demands
                // `Server.AgentSessionFencingToken == fencingToken`, so a poll loop starved past the
                // two-minute lease by a long local build (two pipelines sharing one runner) came back
                // to find its running task parked as "restarted with a new session", its log batches
                // refused and its completion answered 409. Only a genuinely different session id -
                // which a restart always produces, since AgentState mints a new GUID per process -
                // takes the server over and earns a new token.
                var reclaimed = await db.Servers
                    .Where(server => server.Id == serverId
                        && server.AgentSessionId == agentSessionId
                        && (server.AgentSessionLeaseExpiresAt == null
                            || server.AgentSessionLeaseExpiresAt <= now))
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(server => server.AgentSessionLeaseExpiresAt, expiresAt),
                        ct)
                    .ConfigureAwait(false);

                if (reclaimed == 0)
                {
                    await db.Servers
                        .Where(server => server.Id == serverId
                            && server.AgentSessionId != agentSessionId
                            && (server.AgentSessionId == null
                                || server.AgentSessionLeaseExpiresAt == null
                                || server.AgentSessionLeaseExpiresAt <= now))
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(server => server.AgentSessionId, agentSessionId)
                                .SetProperty(server => server.AgentSessionLeaseExpiresAt, expiresAt)
                                .SetProperty(
                                    server => server.AgentSessionFencingToken,
                                    server => server.AgentSessionFencingToken + 1),
                            ct)
                        .ConfigureAwait(false);
                }
            }

            // A fencing token of 0 is not a valid lease: TasksController requires
            // AgentSessionFencingToken > 0 and answers 400/409 otherwise, so an agent holding 0 can
            // never start, log or complete a task. A brand-new server starts at 0, and the only
            // increment lives in the take-over branch above, which fires solely when the session id
            // CHANGES. The heartbeat writes that same session id on its own (ApplyAgentIdentity)
            // without touching the token, so on a freshly enrolled agent the heartbeat wins the race,
            // the take-over never happens and the token stays 0 forever: every task was claimed,
            // refused at start and left to age out in Assigned. Mint the first real token here.
            await db.Servers
                .Where(server => server.Id == serverId
                    && server.AgentSessionId == agentSessionId
                    && server.AgentSessionFencingToken <= 0)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(server => server.AgentSessionFencingToken, 1L),
                    ct)
                .ConfigureAwait(false);

            return await db.Servers.AsNoTracking()
                .Where(server => server.Id == serverId
                    && server.AgentSessionId == agentSessionId
                    && server.AgentSessionLeaseExpiresAt > now)
                .Select(server => (long?)server.AgentSessionFencingToken)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        var trackedServer = await db.Servers.FindAsync([serverId], ct).ConfigureAwait(false);
        if (trackedServer is null)
            return null;
        if (trackedServer.AgentSessionId != agentSessionId)
        {
            if (trackedServer.AgentSessionLeaseExpiresAt > now)
                return null;
            trackedServer.AgentSessionId = agentSessionId;
            trackedServer.AgentSessionFencingToken++;
        }
        // Same floor as the relational path: 0 is never a usable token.
        if (trackedServer.AgentSessionFencingToken <= 0)
            trackedServer.AgentSessionFencingToken = 1;

        // No `else` branch: a lapsed lease reclaimed by the same session id keeps its token, so the
        // agent's own running work stays fenced-in. See the relational path above for the incident.
        trackedServer.AgentSessionLeaseExpiresAt = expiresAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return trackedServer.AgentSessionFencingToken;
    }

    public Task<bool> HasCurrentAgentLeaseAsync(
        int taskId,
        int serverId,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default) =>
        HaveCurrentAgentLeasesAsync([taskId], serverId, agentSessionId, fencingToken, ct);

    public async Task<bool> HaveCurrentAgentLeasesAsync(
        IReadOnlyCollection<int> taskIds,
        int serverId,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default)
    {
        if (taskIds.Count == 0 || fencingToken <= 0)
            return false;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var matching = await db.Tasks.AsNoTracking()
            .Where(HeldByLiveAgentSession(agentSessionId, fencingToken, now))
            .Where(task => taskIds.Contains(task.Id) && task.ServerId == serverId)
            .Select(task => task.Id)
            .Distinct()
            .CountAsync(ct)
            .ConfigureAwait(false);
        return matching == taskIds.Count;
    }

    public async Task<bool> TryStartTaskWithLeaseAsync(
        ServerTask task,
        string agentSessionId,
        long fencingToken,
        DateTime startedAt,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (db.Database.IsRelational())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var updated = await db.Tasks
                .Where(HeldByLiveAgentSession(agentSessionId, fencingToken, now))
                .Where(candidate => candidate.Id == task.Id
                    && candidate.Status == TaskExecutionStatus.Assigned)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, TaskExecutionStatus.Running)
                        .SetProperty(candidate => candidate.StartedAt, startedAt),
                    ct)
                .ConfigureAwait(false);
            if (updated == 0)
                return false;
            if (!await TryStartStepAsync(task.PipelineStepRunId, startedAt, relational: true, ct).ConfigureAwait(false))
                return false;
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            db.Entry(task).State = EntityState.Detached;
        }
        else if (!await HasCurrentAgentLeaseAsync(
                     task.Id, task.ServerId, agentSessionId, fencingToken, ct).ConfigureAwait(false)
                 || task.Status != TaskExecutionStatus.Assigned)
        {
            return false;
        }

        if (!db.Database.IsRelational()
            && !await TryStartStepAsync(task.PipelineStepRunId, startedAt, relational: false, ct).ConfigureAwait(false))
            return false;
        task.Status = TaskExecutionStatus.Running;
        task.StartedAt = startedAt;
        if (db.Database.IsRelational()) AttachUnchanged(task);
        return true;
    }

    /// <summary>
    /// Hands an assigned-but-never-started task back to the queue (Assigned -> Pending), clearing every
    /// assignment field so a later poll can claim it cleanly. Only the agent session that holds the task
    /// may release it, and only while <see cref="ServerTask.StartedAt"/> is still null: a task that began
    /// executing must never return to the queue, or its side effects would run twice.
    /// </summary>
    public async Task<bool> TryReleaseAssignedTaskWithLeaseAsync(
        ServerTask task,
        string agentSessionId,
        long fencingToken,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (db.Database.IsRelational())
        {
            var released = await db.Tasks
                .Where(HeldByLiveAgentSession(agentSessionId, fencingToken, now))
                .Where(candidate => candidate.Id == task.Id
                    && candidate.Status == TaskExecutionStatus.Assigned
                    && candidate.StartedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, TaskExecutionStatus.Pending)
                        .SetProperty(candidate => candidate.AssignedAt, (DateTime?)null)
                        .SetProperty(candidate => candidate.AssignedAgentSessionId, (string?)null)
                        .SetProperty(candidate => candidate.AssignedAgentSessionFencingToken, (long?)null),
                    ct)
                .ConfigureAwait(false);
            if (released == 0) return false;
            db.Entry(task).State = EntityState.Detached;
            return true;
        }

        if (task.Status != TaskExecutionStatus.Assigned
            || task.StartedAt is not null
            || !await HasCurrentAgentLeaseAsync(
                task.Id, task.ServerId, agentSessionId, fencingToken, ct).ConfigureAwait(false))
        {
            return false;
        }

        task.Status = TaskExecutionStatus.Pending;
        task.AssignedAt = null;
        task.AssignedAgentSessionId = null;
        task.AssignedAgentSessionFencingToken = null;
        return true;
    }

    public async Task<bool> TryStartTaskAsync(
        ServerTask task,
        DateTime startedAt,
        CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var updated = await db.Tasks
                .Where(candidate => candidate.Id == task.Id
                    && (candidate.Status == TaskExecutionStatus.Pending
                        || candidate.Status == TaskExecutionStatus.Assigned))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, TaskExecutionStatus.Running)
                        .SetProperty(candidate => candidate.StartedAt, startedAt),
                    ct)
                .ConfigureAwait(false);
            if (updated == 0) return false;
            if (!await TryStartStepAsync(task.PipelineStepRunId, startedAt, relational: true, ct).ConfigureAwait(false))
                return false;
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            // The answer is the PERSISTED state, not the fact that our own UPDATE matched. A concurrent
            // cancellation runs on another connection, and under READ COMMITTED both operations can
            // legitimately succeed against the row version each of them saw: whoever commits last wins
            // the database, while both callers were told they had won. Re-reading after the commit makes
            // "did I start it?" answer from the row rather than from our optimism, so a start that was
            // overtaken by a cancellation reports false and the two views cannot diverge.
            //
            // This closes the class of last-commit-wins divergences by construction. It is NOT a claim
            // to have reproduced the intermittent PipelineCancellation failure of 2026-08-20: that exact
            // interleaving was never observed under a debugger, and this hardens the invariant whether
            // or not it was the cause.
            var persisted = await db.Tasks.AsNoTracking()
                .Where(candidate => candidate.Id == task.Id)
                .Select(candidate => candidate.Status)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (persisted != TaskExecutionStatus.Running)
            {
                db.Entry(task).State = EntityState.Detached;
                task.Status = persisted;
                AttachUnchanged(task);
                return false;
            }

            db.Entry(task).State = EntityState.Detached;
            task.Status = TaskExecutionStatus.Running;
            task.StartedAt = startedAt;
            AttachUnchanged(task);
            return true;
        }

        if (task.Status is not (TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned))
            return false;
        if (!await TryStartStepAsync(task.PipelineStepRunId, startedAt, relational: false, ct).ConfigureAwait(false))
            return false;
        task.Status = TaskExecutionStatus.Running;
        task.StartedAt = startedAt;
        return true;
    }

    public async Task<bool> TryCompleteTaskWithLeaseAsync(
        ServerTask task,
        string agentSessionId,
        long fencingToken,
        TaskExecutionStatus status,
        int exitCode,
        DateTime completedAt,
        string environmentVariables,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (db.Database.IsRelational())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var updated = await db.Tasks
                .Where(HeldByLiveAgentSession(agentSessionId, fencingToken, now))
                .Where(candidate => candidate.Id == task.Id
                    && (candidate.Status == TaskExecutionStatus.Running
                        || (candidate.Status == TaskExecutionStatus.Assigned
                            && status != TaskExecutionStatus.Success)))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, status)
                        .SetProperty(candidate => candidate.ExitCode, exitCode)
                        .SetProperty(candidate => candidate.CompletedAt, completedAt)
                        .SetProperty(candidate => candidate.EnvironmentVariables, environmentVariables),
                    ct)
                .ConfigureAwait(false);
            if (updated == 0)
                return false;
            if (task.PipelineStepRunId is { } stepRunId)
            {
                var stepUpdated = await db.PipelineStepRuns
                    .Where(step => step.Id == stepRunId
                        && (step.Status == TaskExecutionStatus.Running
                            || (step.Status == TaskExecutionStatus.Assigned
                                && status != TaskExecutionStatus.Success)))
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(step => step.Status, status)
                            .SetProperty(step => step.ExitCode, exitCode)
                            .SetProperty(step => step.CompletedAt, completedAt),
                        ct)
                    .ConfigureAwait(false);
                if (stepUpdated == 0)
                    return false;
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            db.Entry(task).State = EntityState.Detached;
        }
        else if (!await HasCurrentAgentLeaseAsync(
                     task.Id, task.ServerId, agentSessionId, fencingToken, ct).ConfigureAwait(false))
        {
            return false;
        }

        task.Status = status;
        task.ExitCode = exitCode;
        task.CompletedAt = completedAt;
        task.EnvironmentVariables = environmentVariables;
        if (db.Database.IsRelational()) AttachUnchanged(task);
        return true;
    }

    private async Task<bool> TryStartStepAsync(
        int? stepRunId, DateTime startedAt, bool relational, CancellationToken ct)
    {
        if (stepRunId is not { } id) return true;
        if (relational)
        {
            var updated = await db.PipelineStepRuns
                .Where(step => step.Id == id
                    && (step.Status == TaskExecutionStatus.Pending || step.Status == TaskExecutionStatus.Assigned))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(step => step.Status, TaskExecutionStatus.Running)
                    .SetProperty(step => step.StartedAt, startedAt), ct)
                .ConfigureAwait(false);
            return updated > 0;
        }
        var step = await db.PipelineStepRuns.FindAsync([id], ct).ConfigureAwait(false);
        if (step is null || step.Status is not (TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned))
            return false;
        step.Status = TaskExecutionStatus.Running;
        step.StartedAt = startedAt;
        return true;
    }

    private void AttachUnchanged(ServerTask task)
    {
        db.Attach(task);
        db.Entry(task).State = EntityState.Unchanged;
    }

}
