// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Decides whether a ready stage must first wait for an approval, and raises it. Extracted from
/// <see cref="PipelineStageDispatchPlanner"/>, which the R-369 rule below would have taken past the
/// file-size budget (FileSizeAuditTests).
/// </summary>
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

        // PLAN-003 2.7 / recette R-370: a stage with its own delay asks for the PIPELINE's approval,
        // with or without an environment, whatever the environment's policy and whatever was already
        // approved for the run. It is decided on its own: approving it never stands for the
        // environment's approval below, which is still asked when the environment requires one (R-104).
        if (stageDef.ApprovalTimeoutMinutes is > 0)
        {
            existingApprovals = await repo.GetApprovalsAsync(runId, ct).ConfigureAwait(false);
            var own = existingApprovals
                .Where(a => a.Scope == ApprovalScope.Pipeline && a.StageName == stageName)
                .ToList();
            if (own.Count == 0)
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

    /// <summary>
    /// R-369: a deployment stage that names no environment of its own. aetheus-release-fast goes live
    /// from such a stage, and its only environment (prod) sits on the Confirm stage that FOLLOWS the
    /// go-live, so a prod requiring approval was deployed first and asked afterwards. The target of an
    /// environment-less deployment cannot be read from the stage, so it is held for every
    /// approval-requiring environment the run declares anywhere, until each one is approved for the run.
    /// A refusal or an expiry of that approval fails the run before the stage is ever dispatched.
    /// </summary>
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

    /// <summary>
    /// Recette R2-117: when a run starts, every approval-requiring environment it declares (any stage's
    /// <c>environment:</c>) is asked at once, before its first stage, System:Prepare included, is
    /// dispatched. aetheus-release-fast used to ask for prod only when its deployment stage became ready,
    /// after the image build, long after the launch. The run waits (WaitingForApproval) until each one is
    /// approved; an approval given here is the run's (<see cref="IsApprovedForRun"/>), so no later stage
    /// asks for that environment again. A stage's own confirmation (<c>approval_timeout_minutes</c>) is
    /// not asked here: it stays a separate decision, raised when its stage is ready (R-370). A refusal or
    /// an expiry fails the run before anything is dispatched (PipelineRunControlService.ApplyRefusalAsync).
    /// </summary>
    /// <returns><c>true</c> when the run must not dispatch anything on this pass.</returns>
    public Task<bool> RequireLaunchApprovalsAsync(
        int runId, int? projectId, IReadOnlyList<PipelineStageDefinition> runStages, CancellationToken ct) =>
        RequireRunEnvironmentApprovalsAsync(runId, projectId, stageName: null, runStages, ct);

    /// <summary>What a deployment is, for R-369: the <c>deploy</c> execution role, or a step that
    /// deploys (<c>type: deploy</c>, or one of the typed blue-green cutover steps).</summary>
    internal static bool IsDeploymentStage(PipelineStageDefinition stage) =>
        string.Equals(stage.ExecutionRole, "deploy", StringComparison.OrdinalIgnoreCase)
        || stage.Steps.Any(step => step.Type is { } type
            && (type.Equals("deploy", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("bluegreen-", StringComparison.OrdinalIgnoreCase)));

    // Only the environment's own approvals count here: a pipeline approval on a stage of that
    // environment was a different decision (R-104), even when it names the same environment.
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
