// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Queries of <see cref="IPipelineApprovalRepository"/>, moved unchanged from
/// <c>PipelineCoreRepository</c> (read, add, owning pipeline) and <c>PipelineLifecycleRepository</c>
/// (pending list, expiry, stranded runs, closure, decision); the <see cref="IPipelineRepository"/> facade
/// delegates to them.</summary>
internal sealed class PipelineApprovalRepository(AppDbContext db) : IPipelineApprovalRepository
{
    /// <summary>Delay of a pending approval that carries none and names no environment. The gate never
    /// raises one (a pipeline approval always has its own delay); a day, like an environment's default.</summary>
    private const int DefaultApprovalTimeoutMinutes = 1440;

    public async Task<List<PipelineApproval>> GetApprovalsAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineApprovals
            .AsNoTracking()
            .Where(a => a.PipelineRunId == runId)
            .Include(a => a.Environment)
            .Include(a => a.ResolvedByUser)
            .OrderBy(a => a.RequestedAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<PipelineApproval?> FindApprovalAsync(int approvalId, CancellationToken ct = default)
    {
        return await db.PipelineApprovals
            .Include(a => a.Environment)
            .Include(a => a.ResolvedByUser)
            .FirstOrDefaultAsync(a => a.Id == approvalId, ct).ConfigureAwait(false);
    }

    public async Task AddApprovalAsync(PipelineApproval approval, CancellationToken ct = default)
    {
        db.PipelineApprovals.Add(approval);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> GetPipelineIdForApprovalAsync(int approvalId, CancellationToken ct = default)
    {
        return await db.PipelineApprovals.AsNoTracking()
            .Where(a => a.Id == approvalId)
            .Select(a => (int?)a.PipelineRun.PipelineId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>F2: approvals still Pending whose environment's <c>ApprovalTimeoutMinutes</c> has
    /// elapsed since they were requested. Used by the reconcile sweep to auto-expire a request nobody
    /// answered instead of leaving the run parked forever.</summary>
    public async Task<List<int>> GetExpiredPendingApprovalIdsAsync(DateTime now, CancellationToken ct = default)
    {
        return await db.PipelineApprovals
            .AsNoTracking()
            .Where(a => a.Status == ApprovalStatus.Pending)
            // Pipeline approvals can lack an environment, so timeout lookup cannot require an
            // environment join.
            .Select(a => new
            {
                a.Id,
                a.RequestedAt,
                Timeout = a.TimeoutMinutes ?? (a.Environment != null ? a.Environment.ApprovalTimeoutMinutes : DefaultApprovalTimeoutMinutes)
            })
            .Where(x => x.RequestedAt.AddMinutes(x.Timeout) < now)
            .Select(x => x.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Approvals still waiting for a decision on a run that is still waiting for it,
    /// oldest first, limited to the pipelines the caller may read (null = all).</summary>
    public async Task<List<PendingApprovalDto>> GetPendingApprovalsAsync(
        List<int>? accessiblePipelineIds, CancellationToken ct = default)
    {
        return await db.PipelineApprovals
            .AsNoTracking()
            .Where(a => a.Status == ApprovalStatus.Pending
                && a.PipelineRun.Status == PipelineStatus.WaitingForApproval
                && (accessiblePipelineIds == null || accessiblePipelineIds.Contains(a.PipelineRun.PipelineId)))
            .OrderBy(a => a.RequestedAt)
            .Select(a => new PendingApprovalDto
            {
                ApprovalId = a.Id,
                PipelineRunId = a.PipelineRunId,
                PipelineId = a.PipelineRun.PipelineId,
                PipelineName = a.PipelineRun.Pipeline.Name,
                ProjectId = a.PipelineRun.Pipeline.ProjectId,
                StageName = a.StageName,
                Scope = a.Scope,
                EnvironmentName = a.Environment != null ? a.Environment.Name : null,
                RequestedAt = a.RequestedAt
            })
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CloseApprovalsOfEndedRunsAsync(int? runId, DateTime now, string reason, CancellationToken ct = default)
    {
        var query = db.PipelineApprovals
            .Where(a => a.Status == ApprovalStatus.Pending
                        && PipelineStatusFacts.Terminal.Contains(a.PipelineRun.Status));
        if (runId is { } id)
            query = query.Where(a => a.PipelineRunId == id);

        if (db.Database.IsRelational())
            return await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.Status, ApprovalStatus.Rejected)
                    .SetProperty(a => a.ResolvedAt, now)
                    .SetProperty(a => a.ResolvedByUserId, (int?)null)
                    .SetProperty(a => a.Comments, reason), ct)
                .ConfigureAwait(false);

        var pending = await query.ToListAsync(ct).ConfigureAwait(false);
        foreach (var approval in pending)
        {
            approval.Status = ApprovalStatus.Rejected;
            approval.ResolvedAt = now;
            approval.ResolvedByUserId = null;
            approval.Comments = reason;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return pending.Count;
    }

    public async Task<List<StrandedApprovalRun>> GetStrandedApprovalRunsAsync(CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .AsNoTracking()
            .Where(run => run.Status == PipelineStatus.WaitingForApproval
                          && !db.PipelineApprovals.Any(a => a.PipelineRunId == run.Id
                                                           && a.Status == ApprovalStatus.Pending))
            .OrderBy(run => run.Id)
            .Select(run => new StrandedApprovalRun(
                run.Id,
                db.PipelineApprovals
                    .Where(a => a.PipelineRunId == run.Id)
                    .OrderByDescending(a => a.ResolvedAt)
                    .Select(a => (ApprovalStatus?)a.Status)
                    .FirstOrDefault()))
            // Bounded: this is an anomaly list, so a sweep that suddenly matches thousands of runs means
            // something systemic, and re-applying decisions to all of them at once is the wrong answer.
            // The cap keeps one sweep's blast radius small; the next sweep picks the rest up - which only
            // holds if the 50 are chosen deterministically. Without an order, PostgreSQL may hand back the
            // same arbitrary page every sweep and starve the rest forever, so take the oldest runs first:
            // they are the ones that have been parked the longest, and each sweep then makes real progress.
            .Take(50)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<PipelineApproval?> TryResolveApprovalAsync(
        int approvalId, ApprovalStatus decision, DateTime resolvedAt,
        int? resolvedByUserId, string? comments, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            var rows = await db.PipelineApprovals
                .Where(a => a.Id == approvalId && a.Status == ApprovalStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.Status, decision)
                    .SetProperty(a => a.ResolvedAt, resolvedAt)
                    .SetProperty(a => a.ResolvedByUserId, resolvedByUserId)
                    .SetProperty(a => a.Comments, comments), ct)
                .ConfigureAwait(false);
            if (rows != 1) return null;
            var tracked = db.ChangeTracker.Entries<PipelineApproval>().FirstOrDefault(e => e.Entity.Id == approvalId);
            if (tracked is not null) tracked.State = EntityState.Detached;
        }
        else
        {
            var approval = await db.PipelineApprovals
                .FirstOrDefaultAsync(a => a.Id == approvalId && a.Status == ApprovalStatus.Pending, ct)
                .ConfigureAwait(false);
            if (approval is null) return null;
            approval.Status = decision;
            approval.ResolvedAt = resolvedAt;
            approval.ResolvedByUserId = resolvedByUserId;
            approval.Comments = comments;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return await db.PipelineApprovals
            .AsNoTracking()
            .Include(a => a.Environment)
            .Include(a => a.ResolvedByUser)
            .FirstAsync(a => a.Id == approvalId, ct)
            .ConfigureAwait(false);
    }
}
