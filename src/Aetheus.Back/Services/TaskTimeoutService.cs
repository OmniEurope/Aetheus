// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Services;

public sealed class TaskTimeoutService(
    IServiceScopeFactory scopeFactory,
    ILogger<TaskTimeoutService> logger,
    IOptions<BackgroundServicesOptions> options,
    IHubContext<ServerHub> serverHub,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    private readonly TimeSpan _checkInterval = options.Value.TaskCheckInterval;
    private readonly TimeSpan _startupDelay = options.Value.TaskStartupDelay;
    private readonly TimeSpan _taskTimeout = options.Value.TaskTimeout;
    private readonly TimeSpan _assignedStartTimeout = options.Value.AssignedStartTimeout;
    // S-TECH-PTMO: a never-claimed Pending task ages out faster than a claimed/running one.
    private readonly TimeSpan _pendingTimeout = options.Value.PendingTimeout;
    private readonly TimeSpan _offlineAgentGrace = options.Value.OfflineAgentGrace;


    // Only the live colour runs it (decision of 2026-10-02, PostgresLeaderLease).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:task-timeout", RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        if (_startupDelay > TimeSpan.Zero)
            await Task.Delay(_startupDelay, timeProvider, stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_checkInterval);
        // do..while: check immediately on startup, then every interval
        do
        {
            try
            {
                await CheckStaleTasksAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogCritical(
                    ex,
                    "Task recovery sweep failed; the next sweep will retry without stopping the backend");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task CheckStaleTasksAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var timedOutTasks = new List<ServerTask>();
        // One server-status cache for the whole sweep. The three park passes below overlap on the same
        // servers, so without it a fleet of 50 re-read the same column ~150 times every 10 seconds.
        var serverStatuses = new Dictionary<int, ServerStatus?>();

        // A new agent process session on the same server is conclusive evidence that work claimed by
        // the previous process can no longer complete. Reconcile it on this short sweep instead of
        // waiting for the generic 30-minute Running timeout. Legacy agents have no session id and keep
        // the conservative timeout behaviour.
        var supersededSessionTasks = await taskRepo
            .GetTasksFromSupersededAgentSessionsAsync(ct)
            .ConfigureAwait(false);
        var supersededIds = supersededSessionTasks.Select(task => task.Id).ToHashSet();
        var requeuedDeferredCleanup = RequeueDeferredCleanups(supersededSessionTasks);
        // The host-shutdown case (the 539-minute ComplexityProducer): the machine went down
        // mid-pipeline and the agent came back with a NEW session, so every task the old session held
        // lands here, not in the offline sweeps below. The replacement session is alive and can simply
        // redo the work, so pipeline work is requeued for it instead of failing the whole run. Same
        // exclusions as the offline parking: deployment operations never replay, and the grace bounds
        // how old a run may resume.
        requeuedDeferredCleanup |= ParkPipelineWork(
            supersededSessionTasks, supersededSessionTasks.ToList(), now,
            "its agent restarted with a new session");
        MarkSupersededSessionTasks(supersededSessionTasks, now, timedOutTasks);

        // Force-fail a task left in any of the three in-flight states past the timeout: Running that
        // started too long ago, Assigned that an agent claimed but never started, and Pending that no
        // agent ever claimed (offline/crashed before polling). The last case is what left the apache2
        // install stuck forever, the older sweeps never looked at Pending.
        var staleRunning = await taskRepo.GetStaleRunningTasksAsync(_taskTimeout, ct).ConfigureAwait(false);
        staleRunning.RemoveAll(task => supersededIds.Contains(task.Id));
        requeuedDeferredCleanup |= RequeueDeferredCleanups(staleRunning);
        var parked = await ParkPipelineWorkOfOfflineAgentsAsync(taskRepo, staleRunning, now, serverStatuses, ct).ConfigureAwait(false);
        MarkRunningTimedOut(staleRunning, now, timedOutTasks);

        var staleAssigned = await taskRepo.GetStaleAssignedTasksAsync(_assignedStartTimeout, ct).ConfigureAwait(false);
        staleAssigned.RemoveAll(task => supersededIds.Contains(task.Id));
        requeuedDeferredCleanup |= RequeueDeferredCleanups(staleAssigned);
        parked |= await ParkPipelineWorkOfOfflineAgentsAsync(taskRepo, staleAssigned, now, serverStatuses, ct).ConfigureAwait(false);
        MarkTimedOut(staleAssigned, "Assigned", _assignedStartTimeout, now, timedOutTasks);

        // PTMO: Pending uses the shorter dedicated timeout (an unclaimed task signals a dead agent).
        var stalePendingCandidates = await taskRepo.GetStalePendingTasksAsync(_pendingTimeout, ct).ConfigureAwait(false);
        var stalePending = await ConfirmPendingServersAreStillNotPollingAsync(
            taskRepo,
            stalePendingCandidates,
            ct).ConfigureAwait(false);
        // An agent that is not claiming right now is not necessarily gone. It may be restarting after
        // its own self-repair, or after a service restart, which is a hiccup rather than an outage and
        // leaves the server reporting Online with a lapsed polling lease. Parking only the confirmed
        // Offline half let that hiccup kill queued pipeline work: the run was told "it stays queued and
        // resumes when an agent claims it again", then failed minutes later because the Pending sweep
        // ages a task from CreatedAt, which a re-queued task never resets. ConfirmPendingServersAre
        // StillNotPollingAsync has already proven these agents are not claiming, so the offline
        // contract applies unchanged: pipeline-linked, replayable, and bounded by the same grace. A
        // one-off server task still ages out, because nobody is waiting to resume it.
        parked |= ParkPipelineWork(stalePending, stalePending, now, "its agent is not claiming work right now");
        MarkTimedOut(stalePending, "Pending", _pendingTimeout, now, timedOutTasks);

        requeuedDeferredCleanup |= parked;

        if (timedOutTasks.Count > 0 || requeuedDeferredCleanup)
        {
            await taskRepo.SaveChangesAsync(ct).ConfigureAwait(false);

            // Push TaskCompleted so the top-bar tracker drops the chip live, instead of leaving a dead
            // task spinning until the next reconnect/reseed.
            if (timedOutTasks.Count > 0)
                await BroadcastTimedOutAsync(timedOutTasks, ct).ConfigureAwait(false);
        }

        // Advance pipeline for any timed-out tasks linked to a pipeline run
        var pipelineTasks = timedOutTasks
            .Where(t => t.PipelineRunId.HasValue && t.PipelineStepRunId.HasValue)
            .ToList();

        if (pipelineTasks.Count > 0)
        {
            var pipelineRunService = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();

            // Pre-load all pipeline step runs in a single query instead of one per task.
            var stepRunIds = pipelineTasks.Select(t => t.PipelineStepRunId!.Value).Distinct().ToList();
            var stepRunMap = await taskRepo.FindPipelineStepRunsByIdsAsync(stepRunIds, ct).ConfigureAwait(false);

            var advanceTasks = new List<(int RunId, string StageName)>();
            foreach (var task in pipelineTasks)
            {
                if (stepRunMap.TryGetValue(task.PipelineStepRunId!.Value, out var stepRun)
                    && stepRun.Status is not (TaskExecutionStatus.Success or TaskExecutionStatus.Failed or TaskExecutionStatus.Timeout))
                {
                    stepRun.Status = TaskExecutionStatus.Timeout;
                    stepRun.FailureCode ??= task.FailureCode;
                    stepRun.FailureReason ??= task.FailureReason;
                    stepRun.CompletedAt = now;
                    advanceTasks.Add((task.PipelineRunId!.Value, stepRun.StageName));

                    logger.LogInformation("Advancing pipeline run #{RunId} after task #{TaskId} timeout (stage: {Stage})",
                        task.PipelineRunId, task.Id, stepRun.StageName);
                }
            }

            // Batch-save all step run status changes in one round-trip.
            if (advanceTasks.Count > 0)
                await taskRepo.SaveChangesAsync(ct).ConfigureAwait(false);

            foreach (var (runId, stageName) in advanceTasks)
            {
                try
                {
                    await pipelineRunService.AdvanceStageAsync(runId, stageName, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.LogCritical(
                        ex,
                        "Pipeline run #{RunId} failed while advancing timed-out stage {StageName}; recovery will retry without stopping the backend",
                        runId,
                        stageName);
                }
            }
        }

        // S-TECH-ARCR: a post-stage artifact-collection task carries a run id but NO step run, so it is
        // excluded from the step-advance loop above. Since the run now WAITS for it before advancing, a
        // collection task that times out (its agent died mid-collect) would otherwise hang the run
        // forever. Fail those runs closed: a CI without its declared artifact is not successful.
        var stuckArtifactRuns = timedOutTasks
            .Where(t => t.PipelineRunId.HasValue && t.PipelineStepRunId is null
                        && t.Operation == OperationKind.PipelineCollectArtifacts)
            .Select(t => t.PipelineRunId!.Value)
            .Distinct()
            .ToList();

        if (stuckArtifactRuns.Count > 0)
        {
            var pipelineRunService = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            foreach (var runId in stuckArtifactRuns)
            {
                try
                {
                    logger.LogWarning("Failing pipeline run #{RunId} after its artifact-collection task timed out", runId);
                    await pipelineRunService.ContinueAfterArtifactCollectionAsync(
                        runId, TaskExecutionStatus.Timeout, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.LogCritical(
                        ex,
                        "Pipeline run #{RunId} failed while settling a timed-out artifact collection; recovery will retry without stopping the backend",
                        runId);
                }
            }
        }
    }

    /// <summary>
    /// Suspends pipeline work whose agent is offline instead of failing it, and removes it from the
    /// list the caller is about to time out. The task returns to Pending, so the agent picks it up
    /// by itself once it reconnects and the run continues where it stopped.
    ///
    /// Deliberately narrow. Only pipeline-linked tasks park: a one-off server task has nobody waiting
    /// to resume it and still ages out, which is what made the stuck apache2 install visible. Only an
    /// agent confirmed offline parks: a task failing while its agent answers is a real failure. Only
    /// within <see cref="BackgroundServicesOptions.OfflineAgentGrace"/>: waiting forever is not a
    /// result. And never a deployment operation, which cannot be replayed blind.
    /// </summary>
    /// <returns><c>true</c> when at least one task was parked, so the caller saves.</returns>
    private async Task<bool> ParkPipelineWorkOfOfflineAgentsAsync(
        ITaskRepository taskRepo,
        List<ServerTask> tasks,
        DateTime now,
        Dictionary<int, ServerStatus?> serverStatuses,
        CancellationToken ct)
    {
        if (_offlineAgentGrace <= TimeSpan.Zero || tasks.Count == 0) return false;

        var confirmedOffline = new List<ServerTask>();
        foreach (var serverGroup in tasks.GroupBy(task => task.ServerId))
        {
            // Cached for the duration of ONE sweep: this runs three times per sweep (Running, Assigned,
            // Pending) and the same server appears in more than one of those lists, so the status was
            // re-read up to three times every ten seconds for a single column.
            if (!serverStatuses.TryGetValue(serverGroup.Key, out var status))
            {
                status = await taskRepo.GetServerStatusAsync(serverGroup.Key, ct).ConfigureAwait(false);
                serverStatuses[serverGroup.Key] = status;
            }
            if (status == ServerStatus.Offline) confirmedOffline.AddRange(serverGroup);
        }

        return ParkPipelineWork(tasks, confirmedOffline, now, "its agent is offline");
    }

    /// <summary>
    /// Requeues the eligible pipeline tasks among <paramref name="candidates"/> and removes them from
    /// <paramref name="tasks"/>, so the caller's subsequent Timeout pass no longer sees them.
    /// Eligibility is the contract stated on <see cref="ParkPipelineWorkOfOfflineAgentsAsync"/>:
    /// pipeline-linked, replayable operation, within the grace.
    /// </summary>
    private bool ParkPipelineWork(
        List<ServerTask> tasks, IEnumerable<ServerTask> candidates, DateTime now, string reason)
    {
        if (_offlineAgentGrace <= TimeSpan.Zero) return false;

        var parked = candidates
            .Where(task => task.PipelineRunId.HasValue)
            // BACK-025: only never-started work replays; a started command may already have had its effect.
            .Where(task => PipelineOutageReplayPolicy.MayReplayAfterAnOutage(task.Operation, task.StartedAt is not null))
            .Where(task => now - task.CreatedAt < _offlineAgentGrace)
            .ToList();

        foreach (var task in parked)
        {
            task.Status = TaskExecutionStatus.Pending;
            task.AssignedAt = null;
            task.AssignedAgentSessionId = null;
            task.StartedAt = null;
            task.CompletedAt = null;
            task.ExitCode = null;
            logger.LogWarning(
                "Task #{TaskId} ({TaskName}) of run #{RunId} is on hold: {Reason}. "
                + "It stays queued and resumes when an agent claims it again.",
                task.Id,
                task.Name,
                task.PipelineRunId,
                reason);
        }

        var parkedIds = parked.Select(task => task.Id).ToHashSet();
        tasks.RemoveAll(task => parkedIds.Contains(task.Id));
        return parked.Count > 0;
    }

    private bool RequeueDeferredCleanups(List<ServerTask> tasks)
    {
        var deferred = tasks.Where(task => task.IsDeferredCleanup).ToList();
        foreach (var task in deferred)
        {
            task.Status = TaskExecutionStatus.Pending;
            task.AssignedAt = null;
            task.AssignedAgentSessionId = null;
            task.StartedAt = null;
            task.CompletedAt = null;
            task.ExitCode = null;
            logger.LogWarning(
                "Deferred cleanup task #{TaskId} was requeued for affinity runner #{ServerId}.",
                task.Id,
                task.ServerId);
        }
        tasks.RemoveAll(task => task.IsDeferredCleanup);
        return deferred.Count > 0;
    }

    private async Task<List<ServerTask>> ConfirmPendingServersAreStillNotPollingAsync(
        ITaskRepository taskRepo,
        List<ServerTask> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return [];

        var confirmed = new List<ServerTask>(candidates.Count);
        foreach (var serverGroup in candidates.GroupBy(task => task.ServerId))
        {
            // Re-read the task-polling lease after selecting stale candidates. A claim request can
            // race the repository query; heartbeat status alone is insufficient because a partially
            // alive agent may heartbeat indefinitely while its task loop is dead.
            if (await taskRepo.IsServerTaskPollingActiveAsync(serverGroup.Key, ct).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Keeping {TaskCount} old Pending task(s) queued for actively polling server #{ServerId}",
                    serverGroup.Count(),
                    serverGroup.Key);
                continue;
            }

            confirmed.AddRange(serverGroup);
        }

        return confirmed;
    }

    private void MarkTimedOut(List<ServerTask> staleTasks, string state, TimeSpan timeout, DateTime now, List<ServerTask> accumulator)
    {
        foreach (var task in staleTasks)
        {
            task.Status = TaskExecutionStatus.Timeout;
            task.CompletedAt = now;
            task.EnvironmentVariables = TaskEnvProtection.EmptyEnv;
            logger.LogWarning("Task #{TaskId} ({TaskName}) timed out in {State} state after {Minutes} minutes",
                task.Id, task.Name, state, timeout.TotalMinutes);
            accumulator.Add(task);
        }
    }

    private void MarkSupersededSessionTasks(
        List<ServerTask> tasks,
        DateTime now,
        List<ServerTask> accumulator)
    {
        foreach (var task in tasks)
        {
            task.Status = TaskExecutionStatus.Timeout;
            task.CompletedAt = now;
            task.EnvironmentVariables = TaskEnvProtection.EmptyEnv;
            // BACK-025: a started task is not replayed (ParkPipelineWork); it fails and says why.
            if (task.StartedAt is not null)
            {
                task.FailureCode = TaskFailureCodes.InfrastructureMismatch;
                task.FailureReason = PipelineOutageReplayPolicy.NotReplayedReason;
            }
            logger.LogWarning(
                "Task #{TaskId} ({TaskName}) was orphaned by an agent restart; previous session {PreviousSession} was replaced",
                task.Id,
                task.Name,
                task.AssignedAgentSessionId);
            accumulator.Add(task);
        }
    }

    private void MarkRunningTimedOut(List<ServerTask> staleTasks, DateTime now, List<ServerTask> accumulator)
    {
        foreach (var task in staleTasks)
        {
            var timeout = task.TimeoutSeconds > 0
                ? TimeSpan.FromSeconds(task.TimeoutSeconds)
                : _taskTimeout;
            task.Status = TaskExecutionStatus.Timeout;
            task.CompletedAt = now;
            task.EnvironmentVariables = TaskEnvProtection.EmptyEnv;
            logger.LogWarning(
                "Task #{TaskId} ({TaskName}) exceeded its Running timeout budget of {Seconds} seconds",
                task.Id,
                task.Name,
                timeout.TotalSeconds);
            accumulator.Add(task);
        }
    }

    private async Task BroadcastTimedOutAsync(List<ServerTask> timedOutTasks, CancellationToken ct)
    {
        foreach (var task in timedOutTasks)
        {
            var notification = new TaskCompletedNotification
            {
                TaskId = task.Id,
                ServerId = task.ServerId,
                TaskName = task.Name,
                Status = TaskExecutionStatus.Timeout,
                ExitCode = task.ExitCode,
                Output = null,
                Operation = task.Operation
            };
            await Task.WhenAll(
                serverHub.Clients.Group(HubGroups.Server(task.ServerId)).SendAsync("TaskCompleted", notification, ct),
                serverHub.Clients.Group(HubGroups.AllServers).SendAsync("TaskCompleted", notification, ct)
            ).ConfigureAwait(false);
        }
    }
}
