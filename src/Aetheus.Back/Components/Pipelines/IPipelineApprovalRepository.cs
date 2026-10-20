// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Pipeline approvals: the approvals of a run, the pending list, the expiry and stranded-run sweeps, the
/// closure of an ended run's approvals and the decision itself. The port of the approval service, which
/// needs nothing else of the pipeline data but the run lookups of <see cref="IPipelineRunLookupReader"/>
/// (audit AUD-07). <see cref="IPipelineRepository"/> composes it and delegates to the same queries.
/// </summary>
public interface IPipelineApprovalRepository
{
    Task<List<PipelineApproval>> GetApprovalsAsync(int runId, CancellationToken ct = default);

    Task<PipelineApproval?> FindApprovalAsync(int approvalId, CancellationToken ct = default);

    Task<List<PendingApprovalDto>> GetPendingApprovalsAsync(List<int>? accessiblePipelineIds, CancellationToken ct = default);
    Task<List<int>> GetExpiredPendingApprovalIdsAsync(DateTime now, CancellationToken ct = default);

    /// <summary>Runs sitting in WaitingForApproval with no pending approval left to decide. The decision
    /// resolves the approval row before moving the run, so an interruption between the two strands the
    /// run: nothing is pending, so nothing is offered to approve and the timeout sweep cannot see it
    /// either, since that one only looks at pending rows.</summary>
    Task<List<StrandedApprovalRun>> GetStrandedApprovalRunsAsync(CancellationToken ct = default);

    /// <summary>Closes pending approvals of ended runs as Rejected with <paramref name="reason"/>
    /// and no user. When supplied, <paramref name="runId"/> limits closure to that run.</summary>
    Task<int> CloseApprovalsOfEndedRunsAsync(int? runId, DateTime now, string reason, CancellationToken ct = default);
    Task<PipelineApproval?> TryResolveApprovalAsync(int approvalId, ApprovalStatus decision, DateTime resolvedAt,
        int? resolvedByUserId, string? comments, CancellationToken ct = default);

    Task AddApprovalAsync(PipelineApproval approval, CancellationToken ct = default);

    /// <summary>Returns the parent pipeline id for an approval, or null if the approval does not exist.</summary>
    Task<int?> GetPipelineIdForApprovalAsync(int approvalId, CancellationToken ct = default);
}
