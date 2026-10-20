// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Owns the terminal transitions of a pipeline run: the one place that writes a run's final status,
/// marks the steps that will never execute, and broadcasts the completion the UI listens for.
/// </summary>
public interface IPipelineRunFinalizer
{
    /// <summary>Finalizes a run to <paramref name="status"/>, raising the completion event and evicting
    /// the run's masked secrets.</summary>
    Task CompleteRunAsync(int pipelineRunId, PipelineStatus status, CancellationToken ct);

    /// <summary>Fails a run whose stages found no matching runner, persisting the human-readable reasons.</summary>
    Task FailRunWithUnmatchedStagesAsync(
        int runId, List<string> unmatchedReasons, List<PipelineStepRun> unmatchedStageSteps, CancellationToken ct);

    /// <summary>Fails a run the watchdog found stuck, cancelling its pending tail first.</summary>
    Task FailStuckRunAsync(int runId, CancellationToken ct);

    /// <summary>Stamps a terminal status and completion time on steps that will never run.</summary>
    void MarkStepsAs(List<PipelineStepRun> steps, TaskExecutionStatus status);

    /// <summary>Cancels steps whose stage condition evaluated false, capturing the condition evidence.</summary>
    void MarkStepsConditionNotMet(
        int runId, List<PipelineStepRun> steps, string condition,
        IReadOnlyDictionary<string, string> variables, IReadOnlySet<string> secretKeys);

    /// <summary>Guards the dead-end failure branches: only a Running run may still be failed.</summary>
    Task<bool> IsRunActiveAsync(int runId, CancellationToken ct);

    /// <summary>True when a cancellation was requested on the run's variables.</summary>
    Task<bool> IsCancellationRequestedAsync(int runId, CancellationToken ct);
}

/// <summary>
/// The run finalizer, extracted from <see cref="PipelineRunService"/>. It comes out whole because every
/// terminal transition shares the same shape: write the run status, then publish the completion event.
/// It never dispatches work and never advances a stage; it only closes runs.
///
/// What it does NOT own, contrary to what this comment claimed until the 2026-08-21 audit (A360-27):
/// it does not flush tracked step changes (<see cref="CompleteRunAsync"/> goes straight to the status
/// update, and callers save their own step changes first), and it is not the only place that broadcasts
/// a completion - the scheduler sends <c>PipelineRunCompleted</c> itself on the success path. Stating a
/// single-authority contract the code does not enforce is worse than stating none, because the next
/// caller trusts it.
/// </summary>
public sealed class PipelineRunFinalizer(
    IPipelineRepository repo,
    IHubContext<PipelineHub> pipelineHub,
    IDomainEventDispatcher domainEvents,
    ISecretMaskingService secretMasking,
    TimeProvider timeProvider,
    IPipelineResourceLockRepository? resourceLocks = null) : IPipelineRunFinalizer
{
    public async Task CompleteRunAsync(int pipelineRunId, PipelineStatus status, CancellationToken ct)
    {
        await repo.UpdatePipelineRunStatusAsync(pipelineRunId, status, ct).ConfigureAwait(false);
        await ReleaseLocksAsync(pipelineRunId, status, ct).ConfigureAwait(false);
        // Ended runs leave no pending approval.
        if (status.IsTerminal())
            await repo.CloseApprovalsOfEndedRunsAsync(
                pipelineRunId, timeProvider.GetUtcNow().UtcDateTime, EndedRunApprovals.Reason, ct).ConfigureAwait(false);
        await domainEvents.DispatchAsync(new PipelineRunCompletedEvent(pipelineRunId, status), ct).ConfigureAwait(false);
        secretMasking.EvictCache(pipelineRunId);

        // Partial, RolledBack and Rejected are completions like Failed: the page has to stop showing a
        // running run. Only the status carried in the message differs, so the client paints its own colour.
        if (status is PipelineStatus.Failed or PipelineStatus.Partial or PipelineStatus.RolledBack or PipelineStatus.Rejected)
        {
            var pipelineId = await repo.GetPipelineIdForRunAsync(pipelineRunId, ct).ConfigureAwait(false);
            var groups = HubGroups.PipelineRunUpdates(pipelineRunId, pipelineId);
            await pipelineHub.Clients.Groups(groups).SendAsync("PipelineRunCompleted", pipelineRunId, status, ct).ConfigureAwait(false);
        }
        else if (status == PipelineStatus.Cancelled)
        {
            var pipelineId = await repo.GetPipelineIdForRunAsync(pipelineRunId, ct).ConfigureAwait(false);
            await pipelineHub.Clients
                .Groups(HubGroups.PipelineRunUpdates(pipelineRunId, pipelineId))
                .SendAsync("PipelineRunCancelled", pipelineRunId, ct)
                .ConfigureAwait(false);
        }
    }

    public async Task FailRunWithUnmatchedStagesAsync(
        int runId, List<string> unmatchedReasons, List<PipelineStepRun> unmatchedStageSteps, CancellationToken ct)
    {
        if (unmatchedReasons.Count > 0)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var failureReason = string.Join(" ", unmatchedReasons);
            foreach (var step in unmatchedStageSteps)
            {
                MarkSystemStepFailed(
                    step,
                    TaskFailureCodes.InfrastructureMismatch,
                    failureReason,
                    now);
            }
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);

            // Persist the no-matching-server reason through a tracked repository update because the
            // loaded run is untracked.
            await repo.AppendRunWarningsAsync(runId, unmatchedReasons, ct).ConfigureAwait(false);
        }

        // Flush any tracked step-status changes (e.g. a step that failed synchronously at dispatch and
        // was never saved) BEFORE UpdatePipelineRunStatusAsync: its Failed-status safety net queries the
        // DB for a failed step, and a still-tracked-but-unflushed Failed step would be invisible to it,
        // risking a wrongful override back to Success. Saving here makes the terminal state authoritative.
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        await repo.UpdatePipelineRunStatusAsync(runId, PipelineStatus.Failed, ct).ConfigureAwait(false);
        await ReleaseLocksAsync(runId, PipelineStatus.Failed, ct).ConfigureAwait(false);
        var pipelineId = await repo.GetPipelineIdForRunAsync(runId, ct).ConfigureAwait(false);
        var groups = HubGroups.PipelineRunUpdates(runId, pipelineId);
        await pipelineHub.Clients.Groups(groups).SendAsync("PipelineRunCompleted", runId, PipelineStatus.Failed, ct).ConfigureAwait(false);
    }

    /// <summary>Terminal runs release environment locks eagerly; inactive owners also cease
    /// blocking through lock-repository checks.</summary>
    private async Task ReleaseLocksAsync(int runId, PipelineStatus status, CancellationToken ct)
    {
        if (resourceLocks is null || !status.IsTerminal()) return;
        await resourceLocks.ReleaseAsync(runId, ct).ConfigureAwait(false);
    }

    public async Task FailStuckRunAsync(int runId, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.Status is not PipelineStatus.Running) return; // already terminal or gone

        // Cancel the pending tail so no orphan steps linger, then finalize Failed through the normal
        // completion path (dispatches PipelineRunCompletedEvent -> unblocks any parent trigger step, and
        // broadcasts PipelineRunCompleted so the UI/top-bar drop the spinning run live).
        await repo.CancelActiveStepRunsAndTasksAsync(runId, ct).ConfigureAwait(false);
        await CompleteRunAsync(runId, PipelineStatus.Failed, ct).ConfigureAwait(false);
    }

    public void MarkStepsAs(List<PipelineStepRun> steps, TaskExecutionStatus status)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var step in steps)
        {
            step.Status = status;
            step.CompletedAt = now;
        }
    }

    public void MarkStepsConditionNotMet(
        int runId, List<PipelineStepRun> steps, string condition,
        IReadOnlyDictionary<string, string> variables, IReadOnlySet<string> secretKeys)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var capturedVariables = PipelineConditionEvidence.CaptureVariables(
            condition, variables, secretKeys, secretMasking.GetRuntimeSecretValues(runId));
        var capturedVariablesJson = PipelineConditionEvidence.SerializeVariables(capturedVariables);
        foreach (var step in steps)
        {
            step.Status = TaskExecutionStatus.Cancelled;
            step.CompletedAt = now;
            step.SkippedCondition = PipelineConditionEvidence.CompactCondition(condition);
            step.SkippedConditionVariablesJson = capturedVariablesJson;
        }
    }

    // Guards the dead-end failure branches so a non-Running run (Cancelled / WaitingForApproval /
    // already completed) is never flipped to Failed by a late scheduling pass.
    public async Task<bool> IsRunActiveAsync(int runId, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        return run?.Status == PipelineStatus.Running;
    }

    public async Task<bool> IsCancellationRequestedAsync(int runId, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        return run is not null && HasCancellationRequest(run.AdditionalVariablesJson);
    }
}
