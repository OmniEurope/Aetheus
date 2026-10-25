// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Determines whether a ready stage needs approval before dispatch.</summary>
internal sealed class PipelineStageApprovalGate(
    IPipelineRepository repo,
    IHubContext<PipelineHub> pipelineHub,
    IDomainEventDispatcher domainEvents,
    TimeProvider timeProvider)
{
    /// <summary><c>true</c> when the stage must not be dispatched on this pass: an approval was raised,
    /// or one raised earlier is still waiting. <paramref name="projectId"/> is the run's project: every
    /// environment is read in it (BACK-018), never a homonym of another project.</summary>
    public async Task<bool> CheckAndCreateApprovalAsync(
        int runId, int? projectId, string stageName, PipelineStageDefinition stageDef,
        IReadOnlyList<PipelineStageDefinition> runStages, CancellationToken ct)
    {
        var env = string.IsNullOrEmpty(stageDef.Environment)
            ? null
            : await repo.FindEnvironmentByNameAsync(stageDef.Environment, projectId, ct).ConfigureAwait(false);
        List<PipelineApproval>? existingApprovals = null;

        // Stage confirmation and environment approval are separate decisions. A stage's own delay
        // requests pipeline approval regardless of prior environment approvals.
        if (stageDef.ApprovalTimeoutMinutes is > 0)
        {
            existingApprovals = await repo.GetApprovalsAsync(runId, ct).ConfigureAwait(false);
            var own = existingApprovals
                .Where(a => a.Scope == ApprovalScope.Pipeline && a.StageName == stageName)
                .ToList();
            // R-39: a confirmation that only ever timed out is asked again when its stage is ready again.
            // The timeout failed the stage, so a stage ready here was reset for a new attempt (a resume,
            // or a retry of the failed steps) and needs a fresh decision. A refusal stays final.
            if (own.Count == 0 || own.All(a => a.Status == ApprovalStatus.TimedOut))
                return await RaiseApprovalAsync(
                    runId, stageName, ApprovalScope.Pipeline, env, stageDef.ApprovalTimeoutMinutes, ct).ConfigureAwait(false);
            // Resume path: the approved stage re-enters dispatch and goes on to the environment policy.
            // Still pending, or refused (the refusal already failed the stage): not dispatched.
            if (!own.Any(a => a.Status == ApprovalStatus.Approved)) return true;
        }

        if (string.IsNullOrEmpty(stageDef.Environment))
            return IsDeploymentStage(stageDef)
                && await RequireRunEnvironmentApprovalsAsync(runId, projectId, stageName, runStages, ct).ConfigureAwait(false);
        if (env is null || !env.RequireApproval) return false;

        existingApprovals ??= await repo.GetApprovalsAsync(runId, ct).ConfigureAwait(false);
        // An approval authorises THE RUN to touch an environment, not one stage of it. Matching on
        // the stage alone made every stage bound to an approval-gated environment raise its own
        // prompt: aetheus-deploy-prod has fourteen stages, all environment: prod, so one deployment
        // demanded fourteen identical manual approvals for a decision the operator took once.
        //
        // The environment is kept in the match on purpose. A run that reached staging and then prod
        // must still ask for prod: what was authorised is prod, not "whatever comes next".
        if (IsApprovedForRun(existingApprovals, env.Id))
            return false;
        if (IsPendingForRun(existingApprovals, env.Id))
            return true;

        return await RaiseApprovalAsync(runId, stageName, ApprovalScope.Environment, env, timeoutMinutes: null, ct).ConfigureAwait(false);
    }

    /// <summary>An environment-less deployment waits for every approval-requiring environment
    /// declared by the run because its target cannot be inferred from the stage. Refusal or expiry
    /// prevents dispatch.</summary>
    private async Task<bool> RequireRunEnvironmentApprovalsAsync(
        int runId, int? projectId, string? stageName, IReadOnlyList<PipelineStageDefinition> runStages,
        CancellationToken ct)
    {
        // First stage naming each environment, in the run's order: what a launch approval is shown for.
        var environments = runStages
            .Where(stage => !string.IsNullOrWhiteSpace(stage.Environment))
            .GroupBy(stage => stage.Environment!, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, FirstStage: group.First().Name))
            .ToList();
        List<PipelineApproval>? existingApprovals = null;
        foreach (var (name, firstStage) in environments)
        {
            var env = await repo.FindEnvironmentByNameAsync(name, projectId, ct).ConfigureAwait(false);
            if (env is null || !env.RequireApproval) continue;
            existingApprovals ??= await repo.GetApprovalsAsync(runId, ct).ConfigureAwait(false);
            if (IsApprovedForRun(existingApprovals, env.Id)) continue;
            // Already asked and still pending: the run is waiting on it, the stage keeps waiting too.
            if (IsPendingForRun(existingApprovals, env.Id))
                return true;
            // One at a time: the run waits on one decision, and the pass after it asks the next one.
            return await RaiseApprovalAsync(
                runId, stageName ?? firstStage, ApprovalScope.Environment, env, timeoutMinutes: null, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Declared environment approvals are requested before any stage dispatch, including
    /// preparation. Stage-specific confirmation remains a separate decision when its stage becomes
    /// ready.</summary> <returns>True while dispatch must wait for approval.</returns>
    public Task<bool> RequireLaunchApprovalsAsync(
        int runId, int? projectId, IReadOnlyList<PipelineStageDefinition> runStages, CancellationToken ct) =>
        RequireRunEnvironmentApprovalsAsync(runId, projectId, stageName: null, runStages, ct);

    /// <summary>Deployment is identified by the <c>deploy</c> execution role or a deployment step,
    /// including typed blue-green cutovers.</summary>
    internal static bool IsDeploymentStage(PipelineStageDefinition stage) =>
        string.Equals(stage.ExecutionRole, "deploy", StringComparison.OrdinalIgnoreCase)
        || stage.Steps.Any(step => step.Type is { } type
            && (type.Equals("deploy", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("bluegreen-", StringComparison.OrdinalIgnoreCase)));

    // Only environment approvals authorize the environment; pipeline-stage confirmations are
    // separate decisions.
    private static bool IsApprovedForRun(List<PipelineApproval> approvals, int environmentId) =>
        approvals.Any(a => a.Scope == ApprovalScope.Environment && a.EnvironmentId == environmentId
                           && a.Status == ApprovalStatus.Approved);

    private static bool IsPendingForRun(List<PipelineApproval> approvals, int environmentId) =>
        approvals.Any(a => a.Scope == ApprovalScope.Environment && a.EnvironmentId == environmentId
                           && a.Status == ApprovalStatus.Pending);

    private async Task<bool> RaiseApprovalAsync(
        int runId, string stageName, ApprovalScope scope, Data.Entities.Environment? env, int? timeoutMinutes,
        CancellationToken ct)
    {
        var approval = new PipelineApproval
        {
            PipelineRunId = runId,
            StageName = stageName,
            Scope = scope,
            EnvironmentId = env?.Id,
            // Null falls back to the environment's delay; a pipeline approval always carries its own.
            TimeoutMinutes = timeoutMinutes,
            // Never set, and nothing else writes it: the row was stored with DateTime's default, so
            // GetExpiredPendingApprovalIdsAsync computed year 1 + ApprovalTimeoutMinutes, which is
            // always in the past. Every production approval therefore expired on the first reconcile
            // sweep, roughly a minute after it was raised, and the run failed as TimedOut before any
            // human could answer it - runs 2307 and 2313 both died that way, each one minute in.
            RequestedAt = timeProvider.GetUtcNow().UtcDateTime
        };
        await repo.AddApprovalAsync(approval, ct).ConfigureAwait(false);

        if (!await repo.TryTransitionPipelineRunStatusAsync(
                runId, PipelineStatus.Running, PipelineStatus.WaitingForApproval, ct).ConfigureAwait(false))
            return false;
        // The stage may have been throttled or waiting for a runner a moment ago; the transition above
        // commits its own raw SQL update outside this pass's SaveChanges, so a stale reason written on
        // an earlier pass would otherwise sit on the row, unreadable while WaitingForApproval masks it,
        // then resurface verbatim the instant approval resumes the run and flips it back to Running -
        // before the resuming dispatch pass gets a chance to overwrite or clear it.
        await repo.SetRunWaitingReasonAsync(runId, null, ct).ConfigureAwait(false);
        var approvalPipelineId = await repo.GetPipelineIdForRunAsync(runId, ct).ConfigureAwait(false);
        var approvalGroups = HubGroups.PipelineRunUpdates(runId, approvalPipelineId);
        await pipelineHub.Clients.Groups(approvalGroups).SendAsync("ApprovalRequired", runId, stageName, env?.Name, ct).ConfigureAwait(false);
        // Approval observers (audit, notifications) are non-blocking.
        domainEvents.Publish(new PipelineApprovalRequestedEvent(runId, stageName, env?.Name));

        return true;
    }
}
