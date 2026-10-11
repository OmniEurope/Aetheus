// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Claims;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Pipelines;

public class PipelineApprovalService(
    IPipelineRepository repo,
    IPipelineRunService runService,
    IHttpContextAccessor httpContextAccessor,
    IHubContext<PipelineHub> pipelineHub,
    IAuditService audit,
    ILogger<PipelineApprovalService> logger,
    TimeProvider timeProvider) : IPipelineApprovalService
{
    public async Task<List<PipelineApprovalDto>> GetApprovalsAsync(int runId, CancellationToken ct = default)
    {
        var approvals = await repo.GetApprovalsAsync(runId, ct).ConfigureAwait(false);
        return approvals.Select(MapApprovalToDto).ToList();
    }

    public Task<List<PendingApprovalDto>> GetPendingApprovalsAsync(
        List<int>? accessiblePipelineIds, CancellationToken ct = default)
        => repo.GetPendingApprovalsAsync(accessiblePipelineIds, ct);

    /// <summary>
    /// Recette R2-116: every decision, a click on Approve or Reject as well as the system's expiry, is
    /// written to the audit log with who made it, on which run, stage and approval, and what came of it.
    /// A decision that is not applied is refused out loud (404, 409 or 400 with the reason) instead of the
    /// bare 404 every refusal used to share: release-fast #2555 was "approved twenty times" with nothing
    /// to tell whether a click had been received, refused, or applied.
    /// </summary>
    public async Task<PipelineApprovalDto> DecideApprovalAsync(
        int approvalId, ApprovalDecisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var approval = await repo.FindApprovalAsync(approvalId, ct).ConfigureAwait(false);
        DecisionOutcome outcome;
        try
        {
            outcome = await ApplyDecisionAsync(approvalId, approval, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory: an unexpected failure is a result of the click too, and the audit is where the
            // user looks for it. Recorded by type only (a message may carry run data the audit must not
            // keep), then rethrown untouched to ErrorHandlingMiddleware.
            await AuditAsync(approvalId, approval, request.Decision, $"failed: {ex.GetType().Name}", ct).ConfigureAwait(false);
            throw;
        }

        await AuditAsync(approvalId, approval, request.Decision, outcome.AuditResult, ct).ConfigureAwait(false);
        return outcome.Decided ?? throw outcome.Refusal!;
    }

    private async Task<DecisionOutcome> ApplyDecisionAsync(
        int approvalId, PipelineApproval? approval, ApprovalDecisionRequest request, CancellationToken ct)
    {
        // F2: TimedOut is a system-issued decision (see PipelineTriggerReconcileService.ExpireStaleApprovalsAsync), never a
        // human choice offered by the UI, but it resolves the run exactly like a Rejected decision -
        // it fails the run and unblocks whoever is waiting on it.
        if (request.Decision is not (ApprovalStatus.Approved or ApprovalStatus.Rejected or ApprovalStatus.TimedOut))
            return DecisionOutcome.Refused(new BadRequestException(
                $"'{request.Decision}' is not a decision; an approval is approved or rejected."));
        if (approval is null)
            return DecisionOutcome.Refused(new NotFoundException($"Approval {approvalId} does not exist."));
        if (approval.Status != ApprovalStatus.Pending)
            return DecisionOutcome.Refused(new ConflictException(AlreadyDecided(approval)));

        // PLAN-005 lot 3 / D34: a decision on a run that has already ended is refused out loud. Approving
        // would try to resume a finished run; rejecting would claim a failure the run did not have.
        // The system's own TimedOut sweep only ever reaches pending approvals, which an ended run no
        // longer has once CloseApprovalsOfEndedRunsAsync has run.
        if ((await repo.GetRunStatusesByIdsAsync([approval.PipelineRunId], ct).ConfigureAwait(false))
                .TryGetValue(approval.PipelineRunId, out var runStatus)
            && runStatus.IsTerminal())
            return DecisionOutcome.Refused(new ConflictException(
                $"Run {approval.PipelineRunId} has already ended ({runStatus}); its approvals can no longer be decided."));

        // F-43: persist who resolved the approval so it surfaces in the audit trail/DTOs.
        var userIdClaim = httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var resolvedByUserId = int.TryParse(userIdClaim, out var resolvedById) ? resolvedById : (int?)null;
        var resolved = await repo.TryResolveApprovalAsync(
            approvalId, request.Decision, timeProvider.GetUtcNow().UtcDateTime,
            resolvedByUserId, request.Comments, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            // Pending a moment ago, decided by another click or the expiry sweep in between.
            var current = await repo.FindApprovalAsync(approvalId, ct).ConfigureAwait(false);
            return DecisionOutcome.Refused(new ConflictException(current is null
                ? $"Approval {approvalId} no longer exists."
                : AlreadyDecided(current)));
        }

        return request.Decision == ApprovalStatus.Approved
            ? await ResumeAsync(approvalId, resolved, ct).ConfigureAwait(false)
            : await RefuseAsync(approvalId, resolved, request.Decision, ct).ConfigureAwait(false);
    }

    private async Task<DecisionOutcome> ResumeAsync(int approvalId, PipelineApproval approval, CancellationToken ct)
    {
        if (!await runService.ResumeAfterApprovalAsync(approval.PipelineRunId, ct).ConfigureAwait(false))
        {
            // The approval is already resolved at this point, so the run is left in WaitingForApproval
            // with nothing pending on it: no decision offered, and invisible to the timeout sweep. Said
            // out loud so it is not silent, and picked up by
            // PipelineTriggerReconcileService.ResolveStrandedApprovalsAsync on its next pass.
            logger.LogError(
                "Approval {ApprovalId} was approved but run {RunId} did not resume; the run is stranded until the reconciler re-applies it",
                approvalId, approval.PipelineRunId);
            return DecisionOutcome.Refused(new ConflictException(
                $"The approval was recorded, but run {approval.PipelineRunId} did not resume: it was no longer waiting "
                + "for an approval. The reconciler re-applies the recorded decision on its next pass."));
        }
        var approvedPipelineId = await repo.GetPipelineIdForRunAsync(approval.PipelineRunId, ct).ConfigureAwait(false);
        var approvedGroups = HubGroups.PipelineRunUpdates(approval.PipelineRunId, approvedPipelineId);
        await pipelineHub.Clients.Groups(approvedGroups).SendAsync("ApprovalResolved", approval.PipelineRunId, approval.StageName, "Approved", ct).ConfigureAwait(false);
        return DecisionOutcome.Applied(MapApprovalToDto(approval), "approved, run resumed");
    }

    private async Task<DecisionOutcome> RefuseAsync(
        int approvalId, PipelineApproval approval, ApprovalStatus decision, CancellationToken ct)
    {
        // PLAN-003 2.7: a stage's own confirmation fails that stage, so its Rollback still runs and
        // the run completes later, from its handlers.
        var outcome = await runService.ApplyRefusalAsync(approval.PipelineRunId, ct).ConfigureAwait(false);
        if (outcome is null)
        {
            // Same stranding as the approved branch above, same reconciler picks it up.
            logger.LogError(
                "Approval {ApprovalId} was {Decision} but run {RunId} did not fail; the run is stranded until the reconciler re-applies it",
                approvalId, decision, approval.PipelineRunId);
            return DecisionOutcome.Refused(new ConflictException(
                $"The decision was recorded, but run {approval.PipelineRunId} did not stop: it was no longer waiting "
                + "for an approval. The reconciler re-applies the recorded decision on its next pass."));
        }
        var rejectedPipelineId = await repo.GetPipelineIdForRunAsync(approval.PipelineRunId, ct).ConfigureAwait(false);
        var rejectedGroups = HubGroups.PipelineRunUpdates(approval.PipelineRunId, rejectedPipelineId);
        var reason = decision == ApprovalStatus.TimedOut ? "TimedOut" : "Rejected";
        await pipelineHub.Clients.Groups(rejectedGroups).SendAsync("ApprovalResolved", approval.PipelineRunId, approval.StageName, reason, ct).ConfigureAwait(false);
        if (outcome == PipelineStatus.Failed)
            await pipelineHub.Clients.Groups(rejectedGroups).SendAsync("PipelineRunCompleted", approval.PipelineRunId, PipelineStatus.Failed, ct).ConfigureAwait(false);
        return DecisionOutcome.Applied(MapApprovalToDto(approval), $"{reason.ToLowerInvariant()}, run {outcome}");
    }

    private static string AlreadyDecided(PipelineApproval approval)
    {
        var by = approval.ResolvedByUser?.Username is { Length: > 0 } username ? $" by {username}" : string.Empty;
        var at = approval.ResolvedAt is { } resolvedAt
            ? $" at {resolvedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC"
            : string.Empty;
        return $"Approval {approval.Id} (run {approval.PipelineRunId}, stage '{approval.StageName}') was already "
            + $"{approval.Status}{by}{at}; there is nothing left to decide.";
    }

    /// <summary>One audit entry per decision: who (the audit service reads the caller), which run,
    /// stage and approval, the decision asked for, and its result.</summary>
    private Task AuditAsync(
        int approvalId, PipelineApproval? approval, ApprovalStatus decision, string result, CancellationToken ct)
    {
        var target = approval is null
            ? $"approval {approvalId}"
            : $"run {approval.PipelineRunId}, stage '{approval.StageName}', {approval.Scope} approval {approvalId}"
              + (approval.Environment?.Name is { } environment ? $" (environment '{environment}')" : string.Empty);
        return audit.LogAsync(
            "ApprovalDecision", "PipelineApproval", approvalId, $"{decision} on {target}: {result}", ct);
    }

    private sealed record DecisionOutcome(PipelineApprovalDto? Decided, Exception? Refusal, string AuditResult)
    {
        public static DecisionOutcome Applied(PipelineApprovalDto decided, string result) => new(decided, null, result);
        public static DecisionOutcome Refused(Exception refusal) => new(null, refusal, $"refused: {refusal.Message}");
    }

    private static PipelineApprovalDto MapApprovalToDto(PipelineApproval a) => new()
    {
        Id = a.Id,
        PipelineRunId = a.PipelineRunId,
        StageName = a.StageName,
        Scope = a.Scope,
        EnvironmentId = a.EnvironmentId,
        EnvironmentName = a.Environment?.Name,
        EnvironmentInstructions = a.Scope == ApprovalScope.Environment ? a.Environment?.ApprovalInstructions : null,
        Status = a.Status,
        RequestedAt = a.RequestedAt,
        ResolvedAt = a.ResolvedAt,
        ResolvedByUserId = a.ResolvedByUserId,
        ResolvedByUsername = a.ResolvedByUser?.Username,
        Comments = a.Comments
    };
}
