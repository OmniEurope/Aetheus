// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Queries of <see cref="IPipelineAutoTriggerRepository"/>, moved unchanged from
/// <c>PipelineLifecycleRepository</c>; the <see cref="IPipelineRepository"/> facade delegates to them. R-11 (decision
/// of 2026-11-10): a pipeline imported from an external repository mirror is manual only, so the webhook and schedule
/// queries never return one, whatever its trigger column says.</summary>
internal sealed class PipelineAutoTriggerRepository(AppDbContext db) : IPipelineAutoTriggerRepository
{
    public async Task<List<Pipeline>> GetWebhookTriggeredPipelinesWithProjectAsync(CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.TriggerType == PipelineTriggerType.Webhook && p.ExternalImportCommit == null)
            .Include(p => p.Project)
                .ThenInclude(pr => pr!.GitConnection)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Pipeline>> GetWebhookTriggeredPipelinesForProjectAsync(int projectId, CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.ProjectId == projectId && p.TriggerType == PipelineTriggerType.Webhook && p.ExternalImportCommit == null)
            .Include(p => p.Project)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Pipeline>> GetScheduledPipelinesAsync(CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.TriggerType == PipelineTriggerType.Schedule && p.ExternalImportCommit == null)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<ScheduledPipelinesFingerprint> GetScheduledPipelinesFingerprintAsync(CancellationToken ct = default)
    {
        // One aggregate row, no pipeline column read. A grouped constant key yields no row when
        // nothing is scheduled, which is the empty fingerprint.
        var summary = await db.Pipelines
            .AsNoTracking()
            .Where(p => p.TriggerType == PipelineTriggerType.Schedule && p.ExternalImportCommit == null)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                LatestUpdate = group.Max(p => (DateTime?)p.UpdatedAt),
                IdSum = group.Sum(p => (long)p.Id)
            })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return summary is null
            ? default
            : new ScheduledPipelinesFingerprint(summary.Count, summary.LatestUpdate, summary.IdSum);
    }

    public async Task<bool> TryAdvanceScheduleCursorAsync(int pipelineId, DateTime to, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational())
        {
            // InMemory (unit tests): no bulk update, so move the tracked entity.
            var tracked = await db.Pipelines.FirstOrDefaultAsync(pipeline => pipeline.Id == pipelineId, ct).ConfigureAwait(false);
            if (tracked is null || tracked.LastScheduledOccurrence >= to) return false;
            tracked.LastScheduledOccurrence = to;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }

        var rows = await db.Pipelines
            .Where(pipeline => pipeline.Id == pipelineId
                && (pipeline.LastScheduledOccurrence == null || pipeline.LastScheduledOccurrence < to))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(pipeline => pipeline.LastScheduledOccurrence, to), ct)
            .ConfigureAwait(false);
        return rows == 1;
    }

    public async Task RestoreScheduleCursorAsync(
        int pipelineId, DateTime claimed, DateTime? previous, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational())
        {
            var tracked = await db.Pipelines.FirstOrDefaultAsync(pipeline => pipeline.Id == pipelineId, ct).ConfigureAwait(false);
            if (tracked is null || tracked.LastScheduledOccurrence != claimed) return;
            tracked.LastScheduledOccurrence = previous;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        await db.Pipelines
            .Where(pipeline => pipeline.Id == pipelineId && pipeline.LastScheduledOccurrence == claimed)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(pipeline => pipeline.LastScheduledOccurrence, previous), ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> HasActiveRunAsync(int pipelineId, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .AnyAsync(r => r.PipelineId == pipelineId &&
                           (r.Status == PipelineStatus.Running || r.Status == PipelineStatus.Pending || r.Status == PipelineStatus.WaitingForApproval || r.Status == PipelineStatus.AwaitingResume), ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> LockPipelineForWebhookAsync(int pipelineId, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational())
            return await db.Pipelines.AnyAsync(pipeline => pipeline.Id == pipelineId, ct).ConfigureAwait(false);

        var rows = await db.Pipelines
            .Where(pipeline => pipeline.Id == pipelineId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(pipeline => pipeline.Name, pipeline => pipeline.Name),
                ct)
            .ConfigureAwait(false);
        return rows == 1;
    }

    public async Task<List<int>> GetActiveRunIdsAsync(int pipelineId, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .Where(run => run.PipelineId == pipelineId
                          && (run.Status == PipelineStatus.Running
                              || run.Status == PipelineStatus.Pending
                              || run.Status == PipelineStatus.WaitingForApproval
                              || run.Status == PipelineStatus.AwaitingResume))
            .OrderBy(run => run.Id)
            .Select(run => run.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<HashSet<int>> GetPipelineIdsWithActiveRunsAsync(CancellationToken ct = default)
    {
        var ids = await db.PipelineRuns
            .Where(r => r.Status == PipelineStatus.Running || r.Status == PipelineStatus.Pending || r.Status == PipelineStatus.WaitingForApproval || r.Status == PipelineStatus.AwaitingResume)
            .Select(r => r.PipelineId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return ids.ToHashSet();
    }
}
