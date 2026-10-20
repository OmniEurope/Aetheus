// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The stage-advancement side of the engine, seen from the operations that have to re-enter it
/// (cancel, resume, retry).
///
/// It is passed to those operations as a <b>parameter</b>, never injected into them: cancellation and
/// advancement call each other, and an injected dependency in both directions would be a DI cycle.
/// During the breakdown <see cref="PipelineRunService"/> satisfies it explicitly; at the end the
/// scheduler does.
/// </summary>
public interface IPipelineRunScheduler
{
    /// <summary>Dispatches the next stage, or finalizes the run when nothing is left to do.</summary>
    Task AdvanceToNextStageOrCompleteAsync(
        int pipelineRunId, IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>Serialized stage advance after a task completed.</summary>
    Task AdvanceStageAsync(
        int pipelineRunId, string completedStageName, IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>Refused or expired stage confirmation fails that stage so its <c>failed()</c>
    /// handlers can execute. No transition occurs unless the run is waiting on that
    /// stage.</summary>
    Task<bool> FailUnconfirmedStageAsync(
        int pipelineRunId, string stageName, IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>Periodic reconcile pass for a run whose completions may have been missed.</summary>
    Task ReconcileRunAsync(int pipelineRunId, IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>Resumes a run once its post-stage artifact collection settled.</summary>
    Task ContinueAfterArtifactCollectionAsync(
        int pipelineRunId, TaskExecutionStatus collectionStatus,
        IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>Completes a waiting `type: trigger` step from its finished child run.</summary>
    Task<bool> ResolveCompletedTriggerStepAsync(
        PipelineStepRun step, PipelineStatus childStatus,
        IReadOnlyDictionary<string, string> childOutputs,
        IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>Re-dispatches the pending stage of an already-open run, scoping secrets per step.</summary>
    Task DispatchNextStageWithScopedSecretsAsync(
        PipelineRun run, PipelineYamlDefinition definition,
        IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>
    /// <see cref="DispatchNextStageWithScopedSecretsAsync"/> for a caller that holds no run lock (resume
    /// after an approval, retry of failed steps): it takes the same per-run serialization as every other
    /// dispatch pass first.
    /// </summary>
    Task DispatchNextStageSerializedAsync(
        PipelineRun run, PipelineYamlDefinition definition,
        IPipelineChildRunLauncher launcher, CancellationToken ct);
}


/// <summary>
/// Advances a run one stage at a time. Every completion, retry, approval and reconciliation pass enters
/// here, and every one of them is serialized per run by <see cref="RunAdvanceLock.Shared"/> - two passes
/// running concurrently for the same run is the double-dispatch bug that lock exists to prevent.
/// </summary>
public sealed class PipelineRunScheduler(
    IPipelineRepository repo,
    IHubContext<PipelineHub> pipelineHub,
    IPipelineVariableResolver variableResolver,
    IPipelineRunFinalizer finalizer,
    IPipelineRunDefinitionParser definitions,
    IPipelineSystemTaskFactory systemTasks,
    IPipelineStageDispatchPlanner planner,
    IEncryptionService encryption,
    IPostgresLeaderLease operationLock,
    TimeProvider timeProvider,
    ILogger<PipelineRunScheduler> logger) : IPipelineRunScheduler
{
    public async Task AdvanceStageAsync(int pipelineRunId, string completedStageName, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        await operationLock.RunSerializedAsync(
            $"pipeline-run-advance:{pipelineRunId}",
            async lockToken =>
            {
                using var runLock = await RunAdvanceLock.Shared.AcquireAsync(pipelineRunId, lockToken).ConfigureAwait(false);
                await AdvanceStageLockedAsync(pipelineRunId, completedStageName, launcher, lockToken).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<bool> FailUnconfirmedStageAsync(
        int pipelineRunId, string stageName, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var failed = false;
        await operationLock.RunSerializedAsync(
            $"pipeline-run-advance:{pipelineRunId}",
            async lockToken =>
            {
                using var runLock = await RunAdvanceLock.Shared.AcquireAsync(pipelineRunId, lockToken).ConfigureAwait(false);
                var steps = (await repo.GetPendingStepRunsAsync(pipelineRunId, lockToken).ConfigureAwait(false))
                    .Where(step => string.Equals(step.StageName, stageName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // Nothing to fail would advance the run as if the stage had passed: the caller closes it instead.
                if (steps.Count == 0) return;
                // Failed BEFORE the run leaves WaitingForApproval, so no pass can dispatch them in between.
                finalizer.MarkStepsAs(steps, TaskExecutionStatus.Failed);
                await repo.SaveChangesAsync(lockToken).ConfigureAwait(false);
                if (!await repo.TryTransitionPipelineRunStatusAsync(
                        pipelineRunId, PipelineStatus.WaitingForApproval, PipelineStatus.Running, lockToken).ConfigureAwait(false))
                    return;
                failed = true;
                await AdvanceStageLockedAsync(pipelineRunId, stageName, launcher, lockToken).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return failed;
    }

    public async Task<bool> ResolveCompletedTriggerStepAsync(
        PipelineStepRun step,
        PipelineStatus childStatus,
        IReadOnlyDictionary<string, string> childOutputs,
        IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(childOutputs);

        var resolved = false;
        await operationLock.RunSerializedAsync(
            $"pipeline-run-advance:{step.PipelineRunId}",
            async lockToken =>
            {
                using var runLock = await RunAdvanceLock.Shared.AcquireAsync(step.PipelineRunId, lockToken).ConfigureAwait(false);
                // Recette R1-80: a Rejected child ran every control to the end; what it found travels
                // up in its outputs (the parent is rejected in turn), and the trigger step itself is
                // not a failure, so the parent goes on with its own controls (R2-146) up to its seal.
                var childCompleted = childStatus is PipelineStatus.Success or PipelineStatus.Rejected;
                var status = childCompleted
                    ? TaskExecutionStatus.Success
                    : TaskExecutionStatus.Failed;
                var exitCode = childCompleted ? 0 : 1;
                var outputsJson = childOutputs.Count == 0
                    ? null
                    : JsonSerializer.Serialize(childOutputs);
                var failureCode = childCompleted
                    ? null
                    : TaskFailureCodes.ToolError;
                var failureReason = childCompleted
                    ? null
                    : $"Triggered run {step.TriggeredRunId} completed with status {childStatus}.";
                resolved = await repo.TryResolveTriggeredStepAsync(
                    step.Id,
                    status,
                    exitCode,
                    outputsJson,
                    failureCode,
                    failureReason,
                    timeProvider.GetUtcNow().UtcDateTime,
                    lockToken).ConfigureAwait(false);
                if (!resolved)
                    return;

                logger.LogInformation(
                    "Trigger step {StepId} (run {ParentRun}) resolved {Status} from child run {ChildRun}",
                    step.Id, step.PipelineRunId, status, step.TriggeredRunId);
                await AdvanceStageLockedAsync(step.PipelineRunId, step.StageName, launcher, lockToken).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return resolved;
    }

    public async Task DispatchNextStageSerializedAsync(
        PipelineRun run, PipelineYamlDefinition definition, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        await operationLock.RunSerializedAsync(
            $"pipeline-run-advance:{run.Id}",
            async lockToken =>
            {
                using var runLock = await RunAdvanceLock.Shared.AcquireAsync(run.Id, lockToken).ConfigureAwait(false);
                await DispatchNextStageWithScopedSecretsAsync(run, definition, launcher, lockToken).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    public async Task ReconcileRunAsync(int pipelineRunId, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        await operationLock.RunSerializedAsync(
            $"pipeline-run-advance:{pipelineRunId}",
            async lockToken =>
            {
                using var runLock = await RunAdvanceLock.Shared.AcquireAsync(pipelineRunId, lockToken).ConfigureAwait(false);
                if (!await repo.IsRunStillRunningAsync(pipelineRunId, lockToken).ConfigureAwait(false)) return;
                await AdvanceToNextStageOrCompleteAsync(pipelineRunId, launcher, lockToken).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    private async Task AdvanceStageLockedAsync(int pipelineRunId, string completedStageName, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        if (!await repo.IsRunStillRunningAsync(pipelineRunId, ct).ConfigureAwait(false)) return;

        var allDone = await repo.AreAllStepsInStageCompletedAsync(pipelineRunId, completedStageName, ct).ConfigureAwait(false);
        if (!allDone) return;

        var failedSteps = await repo.GetFailedStepRunsInStageAsync(pipelineRunId, completedStageName, ct).ConfigureAwait(false);

        if (await TryRetryFailedStepAsync(pipelineRunId, failedSteps, launcher, ct).ConfigureAwait(false))
            return;

        if (HasNonContinuableFailures(failedSteps))
        {
            // User-defined failure/finally stages must run before the pipeline becomes terminal.
            // Otherwise an `always()` teardown remains Pending forever and leaks its environment.
            if (await TryContinueWithFailureHandlersAsync(pipelineRunId, launcher, ct).ConfigureAwait(false))
                return;

            // A workspace cleanup is part of the run, not fire-and-forget work after its terminal
            // transition. Keep the run active until the cleanup reports back; its completion will
            // re-enter this scheduler and finalize the run from the original failed step.
            if (await systemTasks.DispatchCleanupIfPendingAsync(pipelineRunId, ct).ConfigureAwait(false))
                return;
            var finalStatus = await finalizer.IsCancellationRequestedAsync(pipelineRunId, ct).ConfigureAwait(false)
                ? PipelineStatus.Cancelled
                : PipelineStatus.Failed;
            await finalizer.CompleteRunAsync(pipelineRunId, finalStatus, ct).ConfigureAwait(false);
            return;
        }

        // S-TECH-ARCR: if the completed stage produces artifacts, the next stage may consume them on a
        // DIFFERENT agent. Dispatching the next stage before the collection task finishes lets that
        // consumer resolve an artifact that has not been collected yet ("could not resolve the artifact",
        // run #51). So when a collection task is dispatched, WAIT: the next stage is advanced only once
        // that task completes, routed back through ContinueAfterArtifactCollectionAsync.
        if (await DispatchPostStageArtifactCollectionAsync(pipelineRunId, completedStageName, launcher, ct).ConfigureAwait(false))
            return;

        await AdvanceToNextStageOrCompleteAsync(pipelineRunId, launcher, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-entry point after a post-stage artifact-collection task finishes (S-TECH-ARCR). Advances to the
    /// next stage (or completes the run) under the per-run lock, exactly where <see cref="AdvanceStageLockedAsync"/>
    /// deferred it. Artifact publication is part of the producing stage's contract: a failed, cancelled, or
    /// timed-out collection fails the run instead of allowing a fake green with no consumable artifact.
    /// </summary>
    public async Task ContinueAfterArtifactCollectionAsync(
        int pipelineRunId, TaskExecutionStatus collectionStatus, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        using var runLock = await RunAdvanceLock.Shared.AcquireAsync(pipelineRunId, ct).ConfigureAwait(false);
        if (!await repo.IsRunStillRunningAsync(pipelineRunId, ct).ConfigureAwait(false)) return;

        if (collectionStatus != TaskExecutionStatus.Success)
        {
            await FailForMissingArtifactAsync(pipelineRunId,
                $"Artifact collection ended with status {collectionStatus}; the run cannot publish its declared artifact.",
                launcher, ct).ConfigureAwait(false);
            return;
        }

        await AdvanceToNextStageOrCompleteAsync(pipelineRunId, launcher, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// BACK-019: a declared artifact that will never exist fails the run like a failed step does:
    /// failure handlers first, then the workspace cleanup, then the terminal status. Nothing still
    /// pending may consume it, and the failed collection keeps the run from ever ending Success
    /// (<see cref="IPipelineRepository.HasFailedArtifactCollectionAsync"/>).
    /// </summary>
    private async Task FailForMissingArtifactAsync(
        int pipelineRunId, string warning, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        await repo.AppendRunWarningsAsync(pipelineRunId, [warning], ct).ConfigureAwait(false);
        // "Run to end": the missing artifact fails the run (HasFailedArtifactCollectionAsync keeps it
        // from ending Success) but stops nothing else; a step that needs the artifact fails by itself.
        if ((await repo.GetPipelineRunWithPipelineAsync(pipelineRunId, ct).ConfigureAwait(false))?.RunToEnd == true)
        {
            await AdvanceToNextStageOrCompleteAsync(pipelineRunId, launcher, ct).ConfigureAwait(false);
            return;
        }
        if (await TryContinueWithFailureHandlersAsync(pipelineRunId, launcher, ct).ConfigureAwait(false))
            return;
        var blockedSteps = (await repo.GetPendingStepRunsAsync(pipelineRunId, ct).ConfigureAwait(false))
            .Where(step => step.StageName != PipelineRunService.SystemCleanupStage)
            .ToList();
        if (blockedSteps.Count > 0)
        {
            finalizer.MarkStepsAs(blockedSteps, TaskExecutionStatus.Cancelled);
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        if (await systemTasks.DispatchCleanupIfPendingAsync(pipelineRunId, ct).ConfigureAwait(false))
            return;
        await finalizer.CompleteRunAsync(pipelineRunId, PipelineStatus.Failed, ct).ConfigureAwait(false);
    }

    private async Task<bool> TryRetryFailedStepAsync(int pipelineRunId, List<PipelineStepRun> failedSteps, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        PipelineStepRun? retriable = null;
        foreach (var candidate in failedSteps.Where(step => step.RetryCount > 0))
        {
            if (await repo.IsStepRetryEligibleAsync(pipelineRunId, candidate.Id, ct).ConfigureAwait(false))
            {
                retriable = candidate;
                break;
            }
        }
        if (retriable is null) return false;

        retriable.RetryCount--;
        retriable.Status = TaskExecutionStatus.Pending;
        retriable.ExitCode = null;
        retriable.StartedAt = null;
        retriable.CompletedAt = null;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        var retryRun = await repo.GetPipelineRunWithPipelineAsync(pipelineRunId, ct).ConfigureAwait(false);
        if (retryRun?.Pipeline is not null)
        {
            var retryDef = definitions.Parse(retryRun);
            if (retryDef is not null)
            {
                await DispatchNextStageWithScopedSecretsAsync(retryRun, retryDef, launcher, ct).ConfigureAwait(false);
            }
        }
        return true;
    }

    /// <summary>Allowed non-advisory failures produce <see cref="PipelineStatus.Partial"/>, which
    /// stops success-only downstream chains. A completed run whose own assurance verdict refuses
    /// delivery becomes <see cref="PipelineStatus.Rejected"/>.</summary>
    internal static PipelineStatus DecideFinalStatus(
        bool cancelled, bool blockingFailure, bool swallowedFailure, bool rolledBack = false, bool rejected = false)
    {
        if (cancelled) return PipelineStatus.Cancelled;
        if (blockingFailure) return rolledBack ? PipelineStatus.RolledBack : PipelineStatus.Failed;
        if (rejected) return PipelineStatus.Rejected;
        return swallowedFailure ? PipelineStatus.Partial : PipelineStatus.Success;
    }

    /// <summary> The stages that undo a deployment - a failure handler (<c>condition: failed()</c>)
    /// that runs a <c>bluegreen-rollback</c> step or declares <c>deployment_role: rollback</c> (R-14).
    /// Recognised by that rather than by a name, so a failure handler that only notifies never turns a
    /// failed run into a rolled-back one.
    /// </summary>
    internal static List<string> RollbackStageNames(PipelineYamlDefinition definition) =>
        [.. YamlParsingHelper.FlattenJobs(definition).Where(PipelineDeploymentStages.IsRollback).Select(stage => stage.Name)];

    /// <summary>The stages that start the new colour (a <c>bluegreen-up</c> step, or
    /// <c>deployment_role: start</c>).</summary>
    internal static List<string> StartStageNames(PipelineYamlDefinition definition) =>
        [.. YamlParsingHelper.FlattenJobs(definition).Where(PipelineDeploymentStages.IsStart).Select(stage => stage.Name)];

    // Recette R2-146 / R1-80: the run sealed its own result as not deployable, or one of its controls
    // ran its suite and found failures (a required test gate published non-zero, possibly by a child
    // run whose outputs its trigger step carries). An advisory step never refuses anything.
    private async Task<bool> WasRejectedAsync(int pipelineRunId, PipelineYamlDefinition definition, CancellationToken ct)
    {
        var advisoryKeys = AdvisoryStepKeys(definition);
        var outputs = await repo.GetSuccessfulStepOutputsAsync(pipelineRunId, ct).ConfigureAwait(false) ?? [];
        return outputs
            .Where(output => !IsAdvisory(advisoryKeys, output.StageName, output.StepName, matrixLeg: null))
            .Any(output => PipelineControlVerdict.RejectsResult(
                PipelineRunHelpers.DeserializeResolvedVariables(output.OutputVariablesJson)));
    }

    /// <summary>
    /// True when something was deployed and then undone: the new colour was started (its start stage
    /// succeeded) and a rollback stage then succeeded entirely. Deploy-prod 2383 failed at Verify
    /// candidate, before starting anything; its rollback had nothing to undo and the run stays Failed.
    /// </summary>
    private async Task<bool> WasRolledBackAsync(int pipelineRunId, PipelineYamlDefinition definition, CancellationToken ct)
    {
        var rollback = RollbackStageNames(definition);
        var started = StartStageNames(definition);
        return rollback.Count > 0 && started.Count > 0
            && await repo.DidStagesAllSucceedAsync(pipelineRunId, started, ct).ConfigureAwait(false)
            && await repo.DidStagesAllSucceedAsync(pipelineRunId, rollback, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The AI steps declared <c>advisory: true</c> in the run's YAML snapshot, as stage/step keys. Their
    /// step runs carry <c>ContinueOnError</c> so dependents still run; this is what keeps their failure
    /// out of the run status too, since an opinion that could not be given is not a partial delivery.
    /// The rule itself is <see cref="PipelineAdvisorySteps"/>, shared with the run lists.
    /// </summary>
    internal static HashSet<string> AdvisoryStepKeys(PipelineYamlDefinition definition) =>
        PipelineAdvisorySteps.Keys(definition);

    /// <summary>True when a swallowed failure is not an advisory step's (the matrix leg suffix of a
    /// step name is ignored, as the dispatcher does).</summary>
    internal static bool HasNonAdvisoryFailure(
        IEnumerable<PipelineStepRun> continuableFailures, IReadOnlySet<string> advisoryKeys) =>
        continuableFailures.Any(step => !IsAdvisory(advisoryKeys, step.StageName, step.StepName, step.MatrixLeg));

    /// <summary>True when the step (stage + name, matrix leg suffix ignored) is in
    /// <see cref="AdvisoryStepKeys"/>. The one test the run status and the run views all use.</summary>
    internal static bool IsAdvisory(IReadOnlySet<string> advisoryKeys, string stageName, string stepName, string? matrixLeg) =>
        PipelineAdvisorySteps.IsAdvisory(advisoryKeys, stageName, stepName, matrixLeg);

    private static bool HasNonContinuableFailures(List<PipelineStepRun> failedSteps)
    {
        return failedSteps.Count > 0 && !failedSteps.All(s => s.ContinueOnError);
    }

    private async Task<bool> TryContinueWithFailureHandlersAsync(int pipelineRunId, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(pipelineRunId, ct).ConfigureAwait(false);
        if (run?.Pipeline is null) return false;

        var definition = definitions.Parse(run);
        if (definition is null) return false;

        var failureHandlerStages = YamlParsingHelper.FlattenJobs(definition)
            .Where(stage => PipelineStageDispatchPlanner.IsFailureHandlerCondition(stage.Condition))
            .Select(stage => stage.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (failureHandlerStages.Count == 0) return false;

        var pendingSteps = await repo.GetPendingStepRunsAsync(pipelineRunId, ct).ConfigureAwait(false);
        if (!pendingSteps.Any(step => failureHandlerStages.Contains(step.StageName))) return false;

        var blockedSteps = pendingSteps
            .Where(step => step.StageName != PipelineRunService.SystemCleanupStage && !failureHandlerStages.Contains(step.StageName))
            .ToList();
        if (blockedSteps.Count > 0)
        {
            finalizer.MarkStepsAs(blockedSteps, TaskExecutionStatus.Cancelled);
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await AdvanceToNextStageOrCompleteAsync(pipelineRunId, launcher, ct).ConfigureAwait(false);
        return true;
    }


    // Retries and resumes use the same per-step secret scoping as initial dispatch, exposing only
    // referenced secrets.
    public async Task DispatchNextStageWithScopedSecretsAsync(
        PipelineRun run, PipelineYamlDefinition definition,
        IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var (resolvedVars, secretKeys) = await ResolveFullVariablesWithSecretsForRunAsync(run, definition, ct).ConfigureAwait(false);
        var pendingSteps = await repo.GetPendingStepRunsAsync(run.Id, ct).ConfigureAwait(false);
        // Same redundancy as in AdvanceToNextStageOrCompleteAsync: the caller hands us the run, so its
        // pipeline id is already here when the navigation is loaded.
        var pipelineIdForRun = run.Pipeline?.Id
            ?? await repo.GetPipelineIdForRunAsync(run.Id, ct).ConfigureAwait(false);
        var organizationId = pipelineIdForRun is null
            ? null
            : await repo.GetPipelineOrganizationIdAsync(pipelineIdForRun.Value, ct).ConfigureAwait(false);
        await planner.CreateTasksForNextStageAsync(run.Id, definition, resolvedVars, pendingSteps, organizationId, secretKeys, launcher, ct).ConfigureAwait(false);
    }

    public async Task AdvanceToNextStageOrCompleteAsync(
        int pipelineRunId, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(pipelineRunId, ct).ConfigureAwait(false);
        if (run?.Pipeline is null) return;

        var definition = definitions.Parse(run);
        if (definition is null) return;

        var (resolvedVars, secretKeys) = await ResolveFullVariablesWithSecretsForRunAsync(run, definition, ct).ConfigureAwait(false);

        // A360-15: the run was loaded WITH its pipeline three lines above, so asking the database which
        // pipeline this run belongs to was a round trip for a value already in hand.
        var organizationId = await repo
            .GetPipelineOrganizationIdAsync(run.Pipeline.Id, ct).ConfigureAwait(false);

        // Loop: CreateTasksForNextStageAsync may cancel stages (condition=false), which unblocks
        // downstream stages. Keep re-evaluating until either a task is dispatched or no pending
        // steps remain.
        for (var guard = 0; guard < 10; guard++)
        {
            if (guard > 0 && !await finalizer.IsRunActiveAsync(pipelineRunId, ct).ConfigureAwait(false)) return;

            var pendingSteps = await repo.GetPendingStepRunsAsync(pipelineRunId, ct).ConfigureAwait(false);
            if (pendingSteps.Count == 0)
            {
                var continuableFailures =
                    await repo.GetContinuableFailedStepsInRunAsync(pipelineRunId, ct).ConfigureAwait(false) ?? [];
                // A step that only went on because of "run to end" fails the run all the same: the
                // mode tests everything, it never turns a failure into a Partial or a Success.
                var blockingFailure = await repo.HasAnyFailedStepInRunAsync(pipelineRunId, ct).ConfigureAwait(false)
                    || continuableFailures.Any(step => step.ContinueOnErrorForced)
                    || await repo.HasFailedArtifactCollectionAsync(pipelineRunId, ct).ConfigureAwait(false);
                var finalStatus = DecideFinalStatus(
                    cancelled: HasCancellationRequest(run.AdditionalVariablesJson),
                    blockingFailure: blockingFailure,
                    swallowedFailure: HasNonAdvisoryFailure(continuableFailures, AdvisoryStepKeys(definition)),
                    rolledBack: blockingFailure && await WasRolledBackAsync(pipelineRunId, definition, ct).ConfigureAwait(false),
                    rejected: !blockingFailure && await WasRejectedAsync(pipelineRunId, definition, ct).ConfigureAwait(false));
                await finalizer.CompleteRunAsync(pipelineRunId, finalStatus, ct).ConfigureAwait(false);
                if (finalStatus == PipelineStatus.Success)
                    await pipelineHub.Clients.Groups(HubGroups.PipelineRunUpdates(pipelineRunId, run.Pipeline.Id)).SendAsync("PipelineRunCompleted", pipelineRunId, PipelineStatus.Success, ct).ConfigureAwait(false);
                return;
            }

            var hadCancelled = await planner.CreateTasksForNextStageAsync(pipelineRunId, definition, resolvedVars, pendingSteps, organizationId, secretKeys, launcher, ct).ConfigureAwait(false);
            if (!hadCancelled) return;
        }
    }

    /// <summary>Returns true if any stage was cancelled (condition=false), signaling the caller to re-evaluate.</summary>
    // First-dispatch convenience: the caller already resolved (resolvedVars, secretKeys) - reuse them (so
    // run parameters/additionalVars are preserved) while still scoping secrets per step. Computes the
    // pending steps + owning org, then defers to the scoping overload.


    // --- ServerTask creation & dispatch (per-stage steps, system tasks, artifact collection) ---
    /// <returns><c>true</c> if an artifact-collection task was dispatched (the caller must then wait for it
    /// before advancing, S-TECH-ARCR) or the run was failed because its producer is unavailable (BACK-019);
    /// <c>false</c> if the stage has no artifacts or no step that produced them.</returns>
    private async Task<bool> DispatchPostStageArtifactCollectionAsync(int runId, string stageName, IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        if (run?.Pipeline is null) return false;

        var definition = definitions.Parse(run);
        if (definition is null) return false;

        var stageDef = YamlParsingHelper.FlattenJobs(definition).FirstOrDefault(s => s.Name == stageName);
        if (stageDef is null || stageDef.Artifacts.Count == 0) return false;

        var organizationId = await repo.GetPipelineOrganizationIdAsync(run.Pipeline.Id, ct).ConfigureAwait(false);
        var producerServerId = await repo.GetStageProducerServerIdAsync(runId, stageName, ct).ConfigureAwait(false);
        if (producerServerId is null) return false;
        var server = await repo.FindOnlineServerByIdAsync(producerServerId.Value, ct: ct).ConfigureAwait(false);
        if (server is null || organizationId is not null && server.OrganizationId != organizationId.Value)
        {
            // BACK-019: the stage promised artifacts and only its producer holds them. Advancing here
            // let the run end Success with a publication that never happened.
            await FailUncollectableArtifactsAsync(runId, stageDef, producerServerId.Value, launcher, ct).ConfigureAwait(false);
            return true;
        }

        var resolvedVars = await ResolveFullVariablesForRunAsync(run, definition, ct).ConfigureAwait(false);
        InjectStageSystemVariables(resolvedVars, stageName, server);
        repo.TrackTask(BuildArtifactCollectionTask(runId, stageDef, server, resolvedVars));
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Dispatched artifact collection task for run {RunId} stage {Stage}", runId, stageName);
        return true;
    }

    private ServerTask BuildArtifactCollectionTask(
        int runId, PipelineStageDefinition stageDef, Server server, Dictionary<string, string> resolvedVars)
    {
        var isWindows = OsTypeHelper.IsWindows(server.OsType, server.OsDescription);
        if (isWindows && resolvedVars.TryGetValue("WORKSPACE", out var ws) && !ws.Contains(":\\"))
            resolvedVars["WORKSPACE"] = PipelineCommandBuilder.GetDefaultWorkspace(runId, isWindows: true);
        var isContainer = stageDef.Isolation?.IsContainer == true;
        var workspace = isContainer
            ? PipelineSystemTaskFactory.ContainerWorkspace
            : resolvedVars.GetValueOrDefault("WORKSPACE", "");

        var resolvedPatterns = stageDef.Artifacts
            .Select(p => SubstituteVariables(p, resolvedVars))
            .ToList();

        var artifactVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The stage's declared artifact_name when it has one, so a stage can be renamed without
            // breaking the pipelines and retained releases that restore its output by name.
            ["AETHEUS_ARTIFACT_NAME"] = StageArtifactName(stageDef),
            ["AETHEUS_RUN_ID"] = runId.ToString(),
            ["AETHEUS_STAGE_NAME"] = stageDef.Name,
            ["AETHEUS_WORKING_DIR"] = workspace,
            ["AETHEUS_WORKSPACE_MODE"] = isContainer ? "container" : "process"
        };

        return new ServerTask
        {
            ServerId = server.Id,
            Name = PipelineRunStatusRepository.StageArtifactCollectionTaskName(stageDef.Name),
            Command = JsonSerializer.Serialize(resolvedPatterns),
            PipelineRunId = runId,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(artifactVars)),
            TimeoutSeconds = PipelineCommandBuilder.ArtifactCollectionTimeoutSeconds,
            Operation = OperationKind.PipelineCollectArtifacts
        };
    }

    // The collection is recorded as failed on the producer, never silently dropped: the run page shows
    // why, and the failed collection is what keeps the run from being finalized as Success.
    private async Task FailUncollectableArtifactsAsync(
        int runId, PipelineStageDefinition stageDef, int producerServerId,
        IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var reason = $"Stage '{stageDef.Name}' declares artifacts, but its producer runner (server {producerServerId}) " +
                     "is offline or no longer eligible, so they cannot be collected.";
        repo.TrackTask(new ServerTask
        {
            ServerId = producerServerId,
            Name = PipelineRunStatusRepository.StageArtifactCollectionTaskName(stageDef.Name),
            Command = JsonSerializer.Serialize(stageDef.Artifacts),
            PipelineRunId = runId,
            Operation = OperationKind.PipelineCollectArtifacts,
            Status = TaskExecutionStatus.Failed,
            FailureCode = TaskFailureCodes.InfrastructureMismatch,
            FailureReason = reason,
            CompletedAt = timeProvider.GetUtcNow().UtcDateTime
        });
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogWarning("Run {RunId}: artifacts of stage {Stage} cannot be collected, producer {ServerId} unavailable",
            runId, stageDef.Name, producerServerId);
        await FailForMissingArtifactAsync(runId, reason, launcher, ct).ConfigureAwait(false);
    }

    // --- Variable resolution (delegated to the injected IPipelineVariableResolver) ---
    // F-009: thin wrappers keeping call sites terse while the logic lives in its own type.

    private Task<(Dictionary<string, string> Resolved, List<string> Warnings, HashSet<string> SecretKeys)> ResolveVariablesWithWarningsAsync(
        PipelineYamlDefinition definition, int? projectId, Dictionary<string, string>? additionalVariables, CancellationToken ct,
        int? pipelineId = null, int? runId = null, string? pipelineName = null, int? buildNumber = null)
        => variableResolver.ResolveVariablesWithWarningsAsync(definition, projectId, additionalVariables, ct, pipelineId, runId, pipelineName, buildNumber);

    /// <summary>
    /// The name a stage's artifact bundle is stored under. `{stage}-artifacts` unless the stage
    /// declares `artifact_name`, which is what lets a stage be renamed without breaking the
    /// pipelines and retained releases that restore its output by name.
    /// </summary>
    internal static string StageArtifactName(PipelineStageDefinition stage) =>
        string.IsNullOrWhiteSpace(stage.ArtifactName)
            ? $"{stage.Name}-artifacts"
            : stage.ArtifactName.Trim();

    private static void InjectStageSystemVariables(
        Dictionary<string, string> stageVars, string stageName, Server server)
        => PipelineVariableResolver.InjectStageSystemVariables(stageVars, stageName, server);

    private Task<Dictionary<string, string>> ResolveFullVariablesForRunAsync(
        PipelineRun run, PipelineYamlDefinition definition, CancellationToken ct)
        => variableResolver.ResolveFullVariablesForRunAsync(run, definition, ct);

    private Task<(Dictionary<string, string> Resolved, HashSet<string> SecretKeys)> ResolveFullVariablesWithSecretsForRunAsync(
        PipelineRun run, PipelineYamlDefinition definition, CancellationToken ct)
        => variableResolver.ResolveFullVariablesWithSecretsForRunAsync(run, definition, ct);
}
