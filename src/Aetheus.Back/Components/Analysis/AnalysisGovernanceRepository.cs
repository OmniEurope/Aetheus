// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

internal sealed class AnalysisGovernanceRepository(AppDbContext db)
{
    public Task<List<AnalysisPolicy>> GetPoliciesAsync(int projectId, CancellationToken ct) =>
        db.AnalysisPolicies.AsNoTracking()
            .Where(policy => policy.OrganizationId == null || (policy.ProjectId == null
                ? db.Projects.Any(project => project.Id == projectId && project.OrganizationId == policy.OrganizationId)
                : policy.ProjectId == projectId))
            .OrderByDescending(policy => policy.Priority)
            .ThenBy(policy => policy.Id)
            .ToListAsync(ct);

    public Task<List<AnalysisPolicy>> GetPoliciesAsyncForConfigurationAsync(
        int? organizationId,
        CancellationToken ct) =>
        db.AnalysisPolicies.AsNoTracking()
            .Where(policy => policy.ProjectId == null
                && (policy.OrganizationId == null || policy.OrganizationId == organizationId))
            .OrderByDescending(policy => policy.Priority)
            .ThenBy(policy => policy.Id)
            .ToListAsync(ct);

    public Task<List<AnalysisPolicy>> GetPoliciesForScopeAsync(
        int? organizationId,
        int? projectId,
        CancellationToken ct) =>
        db.AnalysisPolicies.AsNoTracking()
            .Where(policy => policy.OrganizationId == organizationId && policy.ProjectId == projectId)
            .OrderByDescending(policy => policy.Priority)
            .ThenBy(policy => policy.Id)
            .ToListAsync(ct);

    public Task<AnalysisPolicy?> GetPolicyAsync(int policyId, CancellationToken ct) =>
        db.AnalysisPolicies.FirstOrDefaultAsync(policy => policy.Id == policyId, ct);

    public async Task SavePolicyAsync(AnalysisPolicy policy, CancellationToken ct)
    {
        if (policy.Id == 0) db.AnalysisPolicies.Add(policy);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SavePolicyRevisionAsync(AnalysisPolicy policy, DateTime createdAt, CancellationToken ct)
    {
        if (policy.Id == 0)
            throw new InvalidOperationException("A policy must be persisted before its revision can be captured.");
        if (await db.AnalysisPolicyRevisions.AsNoTracking()
            .AnyAsync(item => item.AnalysisPolicyId == policy.Id && item.Version == policy.Version, ct)
            .ConfigureAwait(false)) return;
        var snapshot = AnalysisPolicySnapshot.Serialize(policy);
        db.AnalysisPolicyRevisions.Add(new AnalysisPolicyRevision
        {
            AnalysisPolicyId = policy.Id,
            Version = policy.Version,
            SnapshotJson = snapshot,
            SnapshotHash = AnalysisPolicySnapshot.Hash(snapshot),
            CreatedAt = createdAt
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<List<AnalysisPolicyRevision>> GetPolicyRevisionsAsync(int policyId, CancellationToken ct) =>
        db.AnalysisPolicyRevisions.AsNoTracking()
            .Where(item => item.AnalysisPolicyId == policyId)
            .OrderByDescending(item => item.Version)
            .ToListAsync(ct);

    public Task<AnalysisPolicyRevision?> GetPolicyRevisionAsync(
        int policyId,
        int version,
        CancellationToken ct) =>
        db.AnalysisPolicyRevisions.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.AnalysisPolicyId == policyId && item.Version == version,
                ct);

    public Task<List<AnalysisPolicyException>> GetExceptionsAsync(int projectId, CancellationToken ct) =>
        db.AnalysisPolicyExceptions.AsNoTracking()
            .Where(exception => exception.ProjectId == projectId)
            .OrderByDescending(exception => exception.CreatedAt)
            .ToListAsync(ct);

    public Task<AnalysisPolicyException?> GetExceptionAsync(int exceptionId, CancellationToken ct) =>
        db.AnalysisPolicyExceptions.FirstOrDefaultAsync(exception => exception.Id == exceptionId, ct);

    public async Task SaveExceptionAsync(AnalysisPolicyException exception, CancellationToken ct)
    {
        if (exception.Id == 0) db.AnalysisPolicyExceptions.Add(exception);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<AnalysisFinding?> GetTrackedFindingAsync(int findingId, CancellationToken ct) =>
        db.AnalysisFindings.Include(finding => finding.Decisions)
            // One occurrence of the CVE watch is enough to know whose finding it is (R-24: the length an acceptance takes).
            // R-44: the occurrence is named by its identifier (a limit without an order is not deterministic), and the
            // two collections are read by two queries instead of one cartesian product.
            .Include(finding => finding.Occurrences
                .Where(occurrence => occurrence.ScannerKey == AcceptedRiskDuration.CveWatchScannerKey)
                .OrderBy(occurrence => occurrence.Id)
                .Take(1))
            .AsSplitQuery()
            .FirstOrDefaultAsync(finding => finding.Id == findingId, ct);

    public Task<List<AnalysisFindingDecision>> GetFindingDecisionsAsync(int findingId, CancellationToken ct) =>
        db.AnalysisFindingDecisions.AsNoTracking()
            .Where(decision => decision.AnalysisFindingId == findingId)
            .OrderByDescending(decision => decision.CreatedAt)
            .ToListAsync(ct);

    public async Task SaveFindingDecisionAsync(
        AnalysisFinding finding,
        AnalysisFindingDecision decision,
        CancellationToken ct)
    {
        db.AnalysisFindingDecisions.Add(decision);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<AnalysisDecisionExpired>> EndExpiredFindingDecisionsAsync(DateTime now, int take, CancellationToken ct)
    {
        var due = await db.AnalysisFindingDecisions
            .Include(decision => decision.AnalysisFinding)
            .Where(decision => decision.RevokedAt == null && decision.ExpiresAt != null && decision.ExpiresAt <= now)
            .OrderBy(decision => decision.ExpiresAt)
            .ThenBy(decision => decision.Id)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var ended = new List<AnalysisDecisionExpired>(due.Count);
        foreach (var decision in due)
        {
            decision.RevokedAt = now;
            var finding = decision.AnalysisFinding;
            // Only a status a decision gave goes back to Open: a finding already open stays as it is, and one a
            // scan found fixed stays fixed.
            if (finding.Status.IsDecision())
            {
                finding.Status = AnalysisFindingStatus.Open;
                finding.UpdatedAt = now;
            }
            ended.Add(new AnalysisDecisionExpired(
                decision.Id, finding.Id, decision.OrganizationId, decision.ProjectId, decision.Status, decision.ExpiresAt!.Value));
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ended;
    }

    public async Task ReopenExpiredFindingDecisionsAsync(int projectId, DateTime now, CancellationToken ct)
    {
        var expiredFindingIds = await db.AnalysisFindingDecisions.AsNoTracking()
            .Where(decision => decision.ProjectId == projectId
                && decision.RevokedAt == null
                && decision.ExpiresAt != null
                && decision.ExpiresAt <= now)
            .Select(decision => decision.AnalysisFindingId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (expiredFindingIds.Count == 0) return;
        var findings = await db.AnalysisFindings
            .Where(finding => expiredFindingIds.Contains(finding.Id)
                && finding.Status != AnalysisFindingStatus.Fixed)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var finding in findings)
        {
            finding.Status = AnalysisFindingStatus.Open;
            finding.UpdatedAt = now;
        }
        if (findings.Count > 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<AnalysisExpirationNotice>> GetExpiringItemsAsync(
        DateTime now,
        DateTime before,
        int limit,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 500);
        var exceptions = await db.AnalysisPolicyExceptions.AsNoTracking()
            .Where(item => item.RevokedAt == null && item.ExpirationNotificationSentAt == null
                && item.ExpiresAt > now && item.ExpiresAt <= before)
            .OrderBy(item => item.ExpiresAt)
            .Take(limit)
            .Select(item => new AnalysisExpirationNotice(
                "exception", item.Id, item.OrganizationId, item.ProjectId,
                item.AnalysisFindingId, item.ExpiresAt, item.CreatedByUsername))
            .ToListAsync(ct).ConfigureAwait(false);
        if (exceptions.Count >= limit) return exceptions;
        var decisions = await db.AnalysisFindingDecisions.AsNoTracking()
            .Where(item => item.RevokedAt == null && item.ExpirationNotificationSentAt == null
                && item.ExpiresAt != null && item.ExpiresAt > now && item.ExpiresAt <= before)
            .OrderBy(item => item.ExpiresAt)
            .Take(limit - exceptions.Count)
            .Select(item => new AnalysisExpirationNotice(
                "decision", item.Id, item.OrganizationId, item.ProjectId,
                item.AnalysisFindingId, item.ExpiresAt!.Value, item.CreatedByUsername))
            .ToListAsync(ct).ConfigureAwait(false);
        exceptions.AddRange(decisions);
        return exceptions.OrderBy(item => item.ExpiresAt).ToList();
    }

    public async Task MarkExpirationNotificationSentAsync(
        string kind,
        int id,
        DateTime sentAt,
        CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            _ = kind switch
            {
                "exception" => await db.AnalysisPolicyExceptions
                    .Where(item => item.Id == id && item.ExpirationNotificationSentAt == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        item => item.ExpirationNotificationSentAt, sentAt), ct).ConfigureAwait(false),
                "decision" => await db.AnalysisFindingDecisions
                    .Where(item => item.Id == id && item.ExpirationNotificationSentAt == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        item => item.ExpirationNotificationSentAt, sentAt), ct).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown analysis governance item kind.")
            };
            return;
        }

        if (kind == "exception")
        {
            var item = await db.AnalysisPolicyExceptions.FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.ExpirationNotificationSentAt == null, ct).ConfigureAwait(false);
            if (item is not null) item.ExpirationNotificationSentAt = sentAt;
        }
        else if (kind == "decision")
        {
            var item = await db.AnalysisFindingDecisions.FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.ExpirationNotificationSentAt == null, ct).ConfigureAwait(false);
            if (item is not null) item.ExpirationNotificationSentAt = sentAt;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown analysis governance item kind.");
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
