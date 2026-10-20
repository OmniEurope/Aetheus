// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>The decisions an operator records by hand on a finding: read, decide, and revert.</summary>
public interface IAnalysisFindingDecisionService
{
    Task<List<AnalysisFindingDecisionDto>> GetFindingDecisionsAsync(int findingId, CancellationToken ct = default);

    Task<AnalysisFindingDecisionDto> CreateFindingDecisionAsync(
        int findingId, CreateAnalysisFindingDecisionRequest request, string actor, CancellationToken ct = default);

    /// <summary>Reverts the finding's active decision, which is revoked (kept in the history), and
    /// puts the finding back to Open unless a later scan found it fixed.</summary>
    Task RevokeActiveFindingDecisionAsync(int findingId, string actor, CancellationToken ct = default);
}

/// <summary>
/// Manual finding decisions, apart from the analysis service: a decision moves one finding's status,
/// it never touches a report, a policy or a gate already evaluated.
/// </summary>
public sealed class AnalysisFindingDecisionService(
    IAnalysisRepository repository,
    TimeProvider timeProvider,
    IAuditService audit) : IAnalysisFindingDecisionService
{
    public async Task<List<AnalysisFindingDecisionDto>> GetFindingDecisionsAsync(
        int findingId,
        CancellationToken ct = default) =>
        (await repository.GetFindingDecisionsAsync(findingId, ct).ConfigureAwait(false)).Select(AnalysisMapper.ToDto).ToList();

    public async Task<AnalysisFindingDecisionDto> CreateFindingDecisionAsync(
        int findingId,
        CreateAnalysisFindingDecisionRequest request,
        string actor,
        CancellationToken ct = default)
    {
        if (request.Status is AnalysisFindingStatus.Open or AnalysisFindingStatus.Fixed)
            throw new BadRequestException("Manual decisions are limited to Accepted, FalsePositive or Mitigated.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= now)
            throw new BadRequestException("A finding decision expiration must be in the future.");
        var finding = await repository.GetTrackedFindingAsync(findingId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Analysis finding not found.");
        foreach (var active in finding.Decisions.Where(decision => decision.RevokedAt == null))
            active.RevokedAt = now;
        finding.Status = request.Status;
        finding.ResolvedAt = null;
        finding.UpdatedAt = now;
        var decision = new AnalysisFindingDecision
        {
            OrganizationId = finding.OrganizationId,
            ProjectId = finding.ProjectId,
            AnalysisFindingId = finding.Id,
            Status = request.Status,
            Reason = request.Reason.Trim(),
            CreatedByUsername = actor,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = now
        };
        await repository.SaveFindingDecisionAsync(finding, decision, ct).ConfigureAwait(false);
        await audit.LogAsync("AnalysisFindingDecisionCreated", nameof(AnalysisFinding), finding.Id,
            $"status={request.Status};expires={request.ExpiresAt:O}", ct).ConfigureAwait(false);
        return AnalysisMapper.ToDto(decision);
    }

    public async Task RevokeActiveFindingDecisionAsync(int findingId, string actor, CancellationToken ct = default)
    {
        var finding = await repository.GetTrackedFindingAsync(findingId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Analysis finding not found.");
        var active = finding.Decisions.Where(decision => decision.RevokedAt == null).ToList();
        if (active.Count == 0)
            throw new ConflictException("The finding has no active decision to revert.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var decision in active)
            decision.RevokedAt = now;
        // A finding a later scan no longer saw stays Fixed: reverting the decision does not bring it back.
        if (finding.Status != AnalysisFindingStatus.Fixed)
            finding.Status = AnalysisFindingStatus.Open;
        finding.UpdatedAt = now;
        await repository.SaveTrackedChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("AnalysisFindingDecisionRevoked", nameof(AnalysisFinding), finding.Id,
            $"revoked={string.Join(',', active.Select(decision => decision.Id))};status={finding.Status};actor={actor}", ct).ConfigureAwait(false);
    }
}
