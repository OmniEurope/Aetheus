// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Services;

/// <summary>
/// Self-heals orchestration runs wedged on a <c>type: trigger</c> step. A trigger step waits on a child
/// pipeline run and carries NO ServerTask, so <see cref="TaskTimeoutService"/> (which only sweeps tasks)
/// never touches it: if the child's completion event is lost - a backend restart between the child
/// finishing and <c>PipelineRunCompletedTriggerHandler</c> firing, a missed hook - the parent step stays
/// Running forever and the whole run hangs (the class of the stuck run #49).
///
/// Each sweep re-reads Running trigger steps older than a short grace and, using the child run's current
/// status, mirrors a terminal child onto the step (exactly like the event handler) then re-enters
/// <c>AdvanceStageAsync</c> so the parent stage can progress. A hard <see cref="BackgroundServicesOptions.TriggerStepStuckTimeout"/>
/// backstop force-fails a step whose child is itself wedged, so no run can hang indefinitely.
/// </summary>
public sealed class PipelineTriggerReconcileService(
    IServiceScopeFactory scopeFactory,
    ILogger<PipelineTriggerReconcileService> logger,
    IOptions<BackgroundServicesOptions> options,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    /// <summary>How many sweeps may try to re-apply one stranded run's decision before giving up on it.</summary>
    private const int MaxStrandedAttempts = 5;

    /// <summary>Attempts spent per stranded run. The service is a singleton, so this survives sweeps.</summary>
    private readonly ConcurrentDictionary<int, int> _strandedAttempts = new();

    private readonly TimeSpan _interval = options.Value.TriggerReconcileInterval;
    private readonly TimeSpan _grace = options.Value.TriggerStepGrace;
    private readonly TimeSpan _schedulerGrace = options.Value.SchedulerRecoveryGrace;
    private readonly TimeSpan _stuckTimeout = options.Value.TriggerStepStuckTimeout;
    private readonly TimeSpan _runStuckTimeout = options.Value.RunStuckTimeout;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (leaderLease is null)
            await RunLeaderLoopAsync(stoppingToken).ConfigureAwait(false);
        else
            await leaderLease.RunAsLeaderAsync("aetheus:pipeline-trigger-reconcile", RunLeaderLoopAsync, stoppingToken).ConfigureAwait(false);
    }

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        // do..while: sweep immediately on startup (recovers events lost across the restart), then per interval.
        do
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogCritical(
                    ex,
                    "Pipeline reconciliation sweep failed; the next sweep will retry without stopping the backend");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPipelineRepository>();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await ReconcileTriggerStepsAsync(scope, repo, now, ct).ConfigureAwait(false);
        await ReconcileStalledCancellationsAsync(scope, repo, now, ct).ConfigureAwait(false);
        await ReconcileStalledSchedulingAsync(scope, repo, now, ct).ConfigureAwait(false);
        await FailStuckRunsAsync(scope, repo, now, ct).ConfigureAwait(false);
        // Before the expiry pass: an approval of an ended run is closed, not timed out, and deciding
        // one would now be refused anyway.
        await CloseApprovalsOfEndedRunsAsync(repo, now, ct).ConfigureAwait(false);
        await ExpireStaleApprovalsAsync(scope, repo, now, ct).ConfigureAwait(false);
        await ResolveStrandedApprovalsAsync(scope, repo, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// PLAN-005 lot 3 / D34. The finalizer closes a run's pending approvals when it ends the run, but
    /// not every path that ends a run goes through it (a cancellation, a launch failure), and runs
    /// that ended before this existed, run 2323 among them, still carry one. This sweep closes every
    /// approval still Pending on an ended run, whichever way it ended.
    /// </summary>
    private async Task CloseApprovalsOfEndedRunsAsync(IPipelineRepository repo, DateTime now, CancellationToken ct)
    {
        var closed = await repo.CloseApprovalsOfEndedRunsAsync(null, now, EndedRunApprovals.Reason, ct).ConfigureAwait(false);
        if (closed > 0)
            logger.LogWarning("Closed {Count} approval(s) still pending on runs that had already ended", closed);
    }

    /// <summary>
    /// Finishes an approval decision that was recorded but never applied to its run.
    ///
    /// DecideApprovalAsync resolves the approval row first and only then moves the run, so anything
    /// between the two - a failed transition, a restart - leaves the run in WaitingForApproval with
    /// nothing pending on it. The run page then shows the waiting status and no decision to make,
    /// because there is none, and the timeout sweep above cannot reach it either: that one only reads
    /// pending rows. Run 2152 sat like that for eight days.
    ///
    /// This does not invent a decision. It re-applies the one already recorded: an approved run is
    /// resumed, a rejected or timed-out one is failed. Both operations are the same ones the decide
    /// path runs, and both are no-ops if something else got there first.
    /// </summary>
    private async Task ResolveStrandedApprovalsAsync(
        AsyncServiceScope scope, IPipelineRepository repo, CancellationToken ct)
    {
        var stranded = await repo.GetStrandedApprovalRunsAsync(ct).ConfigureAwait(false) ?? [];
        if (stranded.Count == 0) return;

        var runService = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
        foreach (var (runId, lastDecision) in stranded)
        {
            // A run whose re-application keeps failing is stranded for a reason this sweep cannot fix, and
            // retrying it every tick forever would bury the log under one LogCritical per tick per run
            // while changing nothing. After MaxStrandedAttempts it is left alone, said once, for a human.
            var attempts = _strandedAttempts.AddOrUpdate(runId, 1, (_, count) => count + 1);
            if (attempts > MaxStrandedAttempts)
            {
                if (attempts == MaxStrandedAttempts + 1)
                    logger.LogError(
                        "Run {RunId} did not leave its stranded approval state after {Attempts} attempts; giving up on it",
                        runId, MaxStrandedAttempts);
                continue;
            }

            try
            {
                switch (lastDecision)
                {
                    case ApprovalStatus.Approved:
                        logger.LogWarning(
                            "Run {RunId} was approved but never resumed; re-applying the resume", runId);
                        await runService.ResumeAfterApprovalAsync(runId, ct).ConfigureAwait(false);
                        break;
                    case ApprovalStatus.Rejected or ApprovalStatus.TimedOut:
                        logger.LogWarning(
                            "Run {RunId} was {Decision} but never failed; re-applying the failure",
                            runId, lastDecision);
                        await runService.ApplyRefusalAsync(runId, ct).ConfigureAwait(false);
                        break;
                    default:
                        // No approval row at all. CheckAndCreateApprovalAsync writes one before moving the
                        // run, so this is a row deleted underneath a waiting run rather than a race, and
                        // there is no recorded decision to re-apply. Named, not guessed at.
                        logger.LogError(
                            "Run {RunId} waits for an approval that does not exist; it can only be cancelled by hand",
                            runId);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogCritical(ex, "Run {RunId} failed to leave its stranded approval state", runId);
                continue;
            }

            // Left the list, or at least was acted on without throwing: forget its attempt count so a
            // later, unrelated stranding of the same run starts from a clean budget.
            _strandedAttempts.TryRemove(runId, out _);
        }
    }

    // Pass 4 (F2): a WaitingForApproval run whose environment's ApprovalTimeoutMinutes has elapsed
    // since the request was raised is auto-decided TimedOut, which PipelineApprovalService resolves
    // exactly like a human Rejected decision - the run fails and the person waiting on it is unblocked
    // instead of the run sitting parked forever. Reuses the same decide path a human approval takes
    // (repo.TryResolveApprovalAsync's ExecuteUpdate CAS on Status == Pending), so a decision landing
    // concurrently with this sweep can only win once.
    private async Task ExpireStaleApprovalsAsync(AsyncServiceScope scope, IPipelineRepository repo, DateTime now, CancellationToken ct)
    {
        var expiredIds = await repo.GetExpiredPendingApprovalIdsAsync(now, ct).ConfigureAwait(false) ?? [];
        if (expiredIds.Count == 0) return;

        var approvalService = scope.ServiceProvider.GetRequiredService<IPipelineApprovalService>();
        foreach (var approvalId in expiredIds)
        {
            try
            {
                var decided = await approvalService.DecideApprovalAsync(
                    approvalId,
                    new ApprovalDecisionRequest { Decision = ApprovalStatus.TimedOut, Comments = "Approval timed out: nobody decided within the environment's approval timeout." },
                    ct).ConfigureAwait(false);
                logger.LogWarning("Approval {ApprovalId} (run {RunId}) expired after its timeout; the run was failed", approvalId, decided.PipelineRunId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is ConflictException or NotFoundException)
            {
                // Decided by a human between the read and now, or a run that did not move: the decide path
                // said why (and audited it); a stranded run is the stranded-approval pass's to finish.
                logger.LogWarning("Approval {ApprovalId} was not expired: {Reason}", approvalId, ex.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogCritical(ex, "Approval {ApprovalId} failed to expire cleanly; leaving it pending for a later retry", approvalId);
            }
        }
    }

    private async Task ReconcileStalledCancellationsAsync(
        AsyncServiceScope scope, IPipelineRepository repo, DateTime now, CancellationToken ct)
    {
        var runIds = await repo.GetStalledCancellationRunIdsAsync(now - _schedulerGrace, ct)
            .ConfigureAwait(false);
        if (runIds.Count == 0) return;

        var runService = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
        foreach (var runId in runIds)
        {
            try
            {
                logger.LogWarning(
                    "Run {RunId} has cancellation pending but no in-flight task; reapplying cancellation to release orphan steps",
                    runId);
                await runService.CancelRunAsync(
                    runId, PipelineRunCancellation.BySystem("Cancellation re-applied: no task was left in flight"), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogCritical(
                    ex,
                    "Run {RunId} failed during cancellation recovery; leaving it active for a later retry without stopping the backend",
                    runId);
            }
        }
    }

    // Pass 1: self-heal trigger steps whose child run already reached a terminal state (lost completion
    // event), and backstop-fail those whose child chain is itself wedged past the hard ceiling.
    private async Task ReconcileTriggerStepsAsync(AsyncServiceScope scope, IPipelineRepository repo, DateTime now, CancellationToken ct)
    {
        var steps = await repo.GetStuckRunningTriggerStepsAsync(now - _grace, ct).ConfigureAwait(false);
        if (steps.Count == 0) return;

        var childIds = steps.Select(s => s.TriggeredRunId!.Value).Distinct().ToList();
        var childStatuses = await repo.GetRunStatusesByIdsAsync(childIds, ct).ConfigureAwait(false);

        var runService = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
        var childOutputs = new Dictionary<int, IReadOnlyDictionary<string, string>>();
        foreach (var step in steps)
        {
            var childId = step.TriggeredRunId!.Value;
            var childExists = childStatuses.TryGetValue(childId, out var childStatus);
            var resolved = ResolveTriggerStatus(step, childId, childExists, childStatus, now);

            if (resolved is null) continue;
            var outputs = await ReadChildOutputsAsync(
                repo, childOutputs, childId, resolved.Value, ct).ConfigureAwait(false);

            var transitioned = await runService.ResolveCompletedTriggerStepAsync(
                step,
                resolved == TaskExecutionStatus.Success ? PipelineStatus.Success : PipelineStatus.Failed,
                outputs,
                ct).ConfigureAwait(false);
            if (!transitioned) continue;

            logger.LogInformation(
                "Reconciled trigger step {StepId} (run {ParentRun}) to {Status} from child run {ChildRun} - completion event was lost",
                step.Id, step.PipelineRunId, resolved.Value, childId);
        }
    }

    private TaskExecutionStatus? ResolveTriggerStatus(
        PipelineStepRun step, int childId, bool childExists, PipelineStatus childStatus, DateTime now)
    {
        if (childExists && childStatus == PipelineStatus.Success) return TaskExecutionStatus.Success;
        // A Partial child finished with a failure inside it, so the waiting trigger step fails too:
        // the same rule ResolveCompletedTriggerStepAsync applies on the live path.
        if (childExists && childStatus.IsUnsuccessful()) return TaskExecutionStatus.Failed;
        if (!childExists)
        {
            logger.LogWarning("Trigger step {StepId} (run {ParentRun}) waits on missing child run {ChildRun}; failing it",
                step.Id, step.PipelineRunId, childId);
            return TaskExecutionStatus.Failed;
        }
        if (step.StartedAt is not { } startedAt || (now - startedAt) <= _stuckTimeout) return null;
        logger.LogWarning("Trigger step {StepId} (run {ParentRun}) exceeded {Hours}h waiting on child run {ChildRun} ({ChildStatus}); failing it",
            step.Id, step.PipelineRunId, _stuckTimeout.TotalHours, childId, childStatus);
        return TaskExecutionStatus.Failed;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadChildOutputsAsync(
        IPipelineRepository repo,
        IDictionary<int, IReadOnlyDictionary<string, string>> cache,
        int childId,
        TaskExecutionStatus status,
        CancellationToken ct)
    {
        if (status != TaskExecutionStatus.Success) return new Dictionary<string, string>();
        if (cache.TryGetValue(childId, out var cached)) return cached;
        var collected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var successfulSteps = await repo.GetSuccessfulStepOutputsAsync(childId, ct).ConfigureAwait(false) ?? [];
        foreach (var childStep in successfulSteps)
        {
            foreach (var (name, value) in PipelineRunHelpers.DeserializeResolvedVariables(childStep.OutputVariablesJson))
                collected[name] = value;
        }
        cache[childId] = collected;
        return collected;
    }

    // Pass 2: a task completion can be persisted while its scheduler callback is lost (restart,
    // cancellation race, transient exception). Such a run has Pending steps but no work in flight and
    // therefore no future callback. Re-drive the stage machine after a short grace instead of waiting
    // forever. The run-level lock makes this safe if a delayed normal callback arrives concurrently.
    private async Task ReconcileStalledSchedulingAsync(
        AsyncServiceScope scope, IPipelineRepository repo, DateTime now, CancellationToken ct)
    {
        var runIds = await repo.GetStalledSchedulableRunIdsAsync(now - _schedulerGrace, ct).ConfigureAwait(false);
        if (runIds.Count == 0) return;

        var runService = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
        foreach (var runId in runIds)
        {
            try
            {
                logger.LogWarning("Run {RunId} has pending pipeline steps but no in-flight work; re-driving its scheduler", runId);
                await runService.ReconcileRunAsync(runId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogCritical(
                    ex,
                    "Run {RunId} failed during scheduler recovery; leaving it active for a later retry without stopping the backend",
                    runId);
            }
        }
    }

    // Pass 3: run-level backstop. A run wedged in Running with no in-flight task and no trigger step
    // waiting on a child can never finalize on its own (e.g. a step failed but the run stayed open, or
    // every task completed without the run closing). Force-fail it so it stops spinning forever.
    private async Task FailStuckRunsAsync(AsyncServiceScope scope, IPipelineRepository repo, DateTime now, CancellationToken ct)
    {
        var stuckRunIds = await repo.GetStuckRunningRunIdsAsync(now - _runStuckTimeout, ct).ConfigureAwait(false);
        if (stuckRunIds.Count == 0) return;

        var runService = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
        foreach (var runId in stuckRunIds)
        {
            try
            {
                logger.LogWarning("Run {RunId} wedged in Running with no in-flight work past {Hours}h; force-failing it",
                    runId, _runStuckTimeout.TotalHours);
                await runService.FailStuckRunAsync(runId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogCritical(
                    ex,
                    "Run {RunId} failed during stuck-run finalization; leaving it active for a later retry without stopping the backend",
                    runId);
            }
        }
    }
}
