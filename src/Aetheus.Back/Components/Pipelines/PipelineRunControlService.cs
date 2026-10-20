// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The three operations a human performs on a live run: cancel it, resume it after an approval, retry
/// its failed steps. They share one shape - check the run is in a state that allows the move, make the
/// state transition atomically, then hand control back to the scheduler - which is why they come out
/// together.
/// </summary>
public interface IPipelineRunControlService
{
    /// <summary>Cancels a run and every run it triggered, depth-first through the trigger tree.
    /// Each cancelled run is audited with the cancellation's author and reason.</summary>
    Task<bool> CancelRunAsync(
        int runId, PipelineRunCancellation cancellation, IPipelineRunScheduler scheduler, IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>Moves a run out of WaitingForApproval and re-dispatches it.</summary>
    Task<bool> ResumeAfterApprovalAsync(
        int runId, IPipelineRunScheduler scheduler, IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>Refused or expired environment approval fails the run, while stage confirmation
    /// fails its stage so failure handlers execute first. Returns null if the run cannot leave
    /// WaitingForApproval.</summary>
    Task<PipelineStatus?> ApplyRefusalAsync(
        int runId, IPipelineRunScheduler scheduler, IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>Re-opens a terminal run, resets its failed steps and re-dispatches.</summary>
    /// <returns><c>true</c> when something was actually retried.</returns>
    Task<bool> RetryFailedStepsAsync(
        int runId, IPipelineRunScheduler scheduler, IPipelineChildRunLauncher launcher, CancellationToken ct);
}

/// <inheritdoc cref="IPipelineRunControlService"/>
public sealed class PipelineRunControlService(
    IPipelineRepository repo,
    IHubContext<PipelineHub> pipelineHub,
    IPipelineRunDefinitionParser definitions,
    IAuditService audit) : IPipelineRunControlService
{
    public async Task<bool> CancelRunAsync(
        int runId, PipelineRunCancellation cancellation, IPipelineRunScheduler scheduler, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        var root = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        if (root is null || IsTerminal(root.Status)) return false;
        if (!await CancelSingleRunAsync(root, cancellation, scheduler, launcher, ct).ConfigureAwait(false)) return false;

        var visited = new HashSet<int> { runId };
        var pendingParents = new Queue<int>();
        pendingParents.Enqueue(runId);
        while (pendingParents.TryDequeue(out var parentRunId))
        {
            var childIds = await repo.GetTriggeredChildRunIdsAsync(parentRunId, ct).ConfigureAwait(false);
            foreach (var childId in childIds)
            {
                if (!visited.Add(childId)) continue;
                var child = await repo.GetPipelineRunWithPipelineAsync(childId, ct).ConfigureAwait(false);
                if (child is not null && !IsTerminal(child.Status))
                    await CancelSingleRunAsync(child, cancellation.ForChildOf(parentRunId), scheduler, launcher, ct).ConfigureAwait(false);
                pendingParents.Enqueue(childId);
            }
        }

        return true;
    }

    private async Task<bool> CancelSingleRunAsync(
        PipelineRun run, PipelineRunCancellation cancellation, IPipelineRunScheduler scheduler, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        using var runLock = await RunAdvanceLock.Shared.AcquireAsync(run.Id, ct).ConfigureAwait(false);
        var current = await repo.GetPipelineRunWithPipelineAsync(run.Id, ct).ConfigureAwait(false);
        if (current is null || IsTerminal(current.Status)) return false;
        if (current.Status != PipelineStatus.Running
            && !await repo.TryTransitionPipelineRunStatusAsync(
                    current.Id, current.Status, PipelineStatus.Running, ct).ConfigureAwait(false))
            return false;

        var definition = current.Pipeline is null ? null : definitions.Parse(current);
        // always() teardown, and the failed() stage that rolls a deployment back (decision of
        // 2026-10-02): a cancelled run must not leave an open blue-green transaction behind it.
        var preservedStages = definition is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : YamlParsingHelper.FlattenJobs(definition)
                .Where(stage => string.Equals(
                        stage.Condition?.Trim(), "always()", StringComparison.OrdinalIgnoreCase)
                    || IsDeploymentRollback(stage))
                .Select(stage => stage.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        preservedStages.Add(PipelineSystemStages.Cleanup);

        var cancellationAlreadyRequested = HasCancellationRequest(current.AdditionalVariablesJson);
        await repo.RequestPipelineRunCancellationAsync(current.Id, cancellation, ct).ConfigureAwait(false);
        // Every cancellation request is audited, including repeats, while the run preserves the
        // first request's author.
        await audit.LogAsync("Cancelled", "PipelineRun", current.Id,
            $"{current.Pipeline?.Name} | by {cancellation.By} | {cancellation.Reason}", ct).ConfigureAwait(false);
        await repo.CancelPendingStepRunsExceptStagesAsync(
            current.Id, preservedStages, ct).ConfigureAwait(false);
        if (cancellationAlreadyRequested)
        {
            await repo.CancelOrphanedRunningStepRunsExceptStagesAsync(
                current.Id, preservedStages, ct).ConfigureAwait(false);
        }

        // Cancellation is a durable run state, not a warning. The UI reads the marker above and shows
        // one informational notice while mandatory teardown is settling. Persisting a warning here made
        // every repeated click append the same pseudo-error to WarningsJson.
        if (!await repo.HasAnyRunningStepInRunAsync(current.Id, ct).ConfigureAwait(false))
            await scheduler.AdvanceToNextStageOrCompleteAsync(current.Id, launcher, ct).ConfigureAwait(false);
        return true;
    }

    internal static bool IsDeploymentRollback(PipelineStageDefinition stage) => PipelineDeploymentStages.IsRollback(stage);

    public async Task<bool> ResumeAfterApprovalAsync(
        int runId, IPipelineRunScheduler scheduler, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        if (run?.Status != PipelineStatus.WaitingForApproval || run.Pipeline is null) return false; // F-017: align the null-guard with sibling methods

        if (!await repo.TryTransitionPipelineRunStatusAsync(
                run.Id, PipelineStatus.WaitingForApproval, PipelineStatus.Running, ct).ConfigureAwait(false))
            return false;

        var definition = definitions.Parse(run);
        if (definition is not null)
        {
            // Under the per-run lock every other dispatch pass takes: a completion or a reconcile pass
            // dispatching the same stage at the same moment would otherwise create its tasks twice.
            await scheduler.DispatchNextStageSerializedAsync(run, definition, launcher, ct).ConfigureAwait(false);
        }
        return true;
    }

    public async Task<PipelineStatus?> ApplyRefusalAsync(
        int runId, IPipelineRunScheduler scheduler, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var refused = (await repo.GetApprovalsAsync(runId, ct).ConfigureAwait(false))
            .Where(approval => approval.Status is ApprovalStatus.Rejected or ApprovalStatus.TimedOut)
            .OrderByDescending(approval => approval.ResolvedAt)
            .FirstOrDefault();
        // Only a stage's own confirmation carries its own delay. An environment approval refuses the
        // run before anything it guards has run, so there is nothing to compensate.
        if (refused?.TimeoutMinutes is > 0
            && await scheduler.FailUnconfirmedStageAsync(runId, refused.StageName, launcher, ct).ConfigureAwait(false))
        {
            await repo.AppendRunWarningsAsync(runId,
                [$"Stage '{refused.StageName}' was not confirmed ({refused.Status}); the run fails and its failure handlers run."],
                ct).ConfigureAwait(false);
            return PipelineStatus.Running;
        }
        return await repo.TryTransitionPipelineRunStatusAsync(
                runId, PipelineStatus.WaitingForApproval, PipelineStatus.Failed, ct).ConfigureAwait(false)
            ? PipelineStatus.Failed
            : null;
    }

    public async Task<bool> RetryFailedStepsAsync(
        int runId, IPipelineRunScheduler scheduler, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        if (run?.Pipeline is null) return false;

        // Distinct refusal reasons reach the caller through the non-success response so an invalid
        // retry is distinguishable from an attempted execution.
        if (run.Status is not (PipelineStatus.Failed or PipelineStatus.Cancelled))
            throw new ConflictException(
                $"Only a failed or cancelled run can be retried; this one is {run.Status}.");

        // Repository re-opens the run (Status → Running, CompletedAt → null) and resets the
        // failed step runs plus the downstream tail cancelled during failure finalization to
        // Pending atomically. Zero reset → nothing to retry.
        var reset = await repo.ResetFailedStepRunsAsync(runId, ct).ConfigureAwait(false);
        if (reset == 0)
            throw new ConflictException(
                "This run has no failed step to retry. If you fixed the pipeline definition, use Re-run: "
                + "a run executes the YAML captured when it was triggered (ADR-015), so retrying replays "
                + "the old definition, never the edited one.");

        var definition = definitions.Parse(run);
        if (definition is not null)
        {
            // Same serialization as the resume above: the re-opened run is visible to every other pass.
            await scheduler.DispatchNextStageSerializedAsync(run, definition, launcher, ct).ConfigureAwait(false);
        }

        await pipelineHub.Clients
            .Groups(HubGroups.PipelineRunUpdates(runId, run.Pipeline.Id))
            .SendAsync("PipelineRunStarted", runId, run.Pipeline.Id, ct).ConfigureAwait(false);

        return true;
    }

    private static bool IsTerminal(PipelineStatus status) =>
        status.IsTerminal();
}
