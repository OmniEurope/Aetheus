// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

public interface IPipelineApprovalService
{
    Task<List<PipelineApprovalDto>> GetApprovalsAsync(int runId, CancellationToken ct = default);
    Task<List<PendingApprovalDto>> GetPendingApprovalsAsync(List<int>? accessiblePipelineIds, CancellationToken ct = default);
    /// <summary>Applies a decision and returns the decided approval. A decision that is not applied throws
    /// with its reason: <see cref="BadRequestException"/> (not a decision), <see cref="NotFoundException"/>
    /// (no such approval) or <see cref="ConflictException"/> (already decided, run ended, run not moved).</summary>
    Task<PipelineApprovalDto> DecideApprovalAsync(int approvalId, ApprovalDecisionRequest request, CancellationToken ct = default);

    /// <summary>What the approval gates, to authorize who decides it; null when it does not exist.</summary>
    Task<PipelineApprovalScope?> GetApprovalScopeAsync(int approvalId, CancellationToken ct = default);
}
