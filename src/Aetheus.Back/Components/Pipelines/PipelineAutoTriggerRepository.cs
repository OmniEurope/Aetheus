// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Queries of <see cref="IPipelineAutoTriggerRepository"/>, moved unchanged from
/// <c>PipelineLifecycleRepository</c>; the <see cref="IPipelineRepository"/> facade delegates to them.</summary>
internal sealed class PipelineAutoTriggerRepository(AppDbContext db) : IPipelineAutoTriggerRepository
{
    public async Task<List<Pipeline>> GetWebhookTriggeredPipelinesWithProjectAsync(CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.TriggerType == PipelineTriggerType.Webhook)
            .Include(p => p.Project)
                .ThenInclude(pr => pr!.GitConnection)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Pipeline>> GetWebhookTriggeredPipelinesForProjectAsync(int projectId, CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.ProjectId == projectId && p.TriggerType == PipelineTriggerType.Webhook)
            .Include(p => p.Project)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Pipeline>> GetScheduledPipelinesAsync(CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.TriggerType == PipelineTriggerType.Schedule)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> HasActiveRunAsync(int pipelineId, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .AnyAsync(r => r.PipelineId == pipelineId &&
                           (r.Status == PipelineStatus.Running || r.Status == PipelineStatus.Pending || r.Status == PipelineStatus.WaitingForApproval), ct)
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
                              || run.Status == PipelineStatus.WaitingForApproval))
            .OrderBy(run => run.Id)
            .Select(run => run.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<HashSet<int>> GetPipelineIdsWithActiveRunsAsync(CancellationToken ct = default)
    {
        var ids = await db.PipelineRuns
            .Where(r => r.Status == PipelineStatus.Running || r.Status == PipelineStatus.Pending || r.Status == PipelineStatus.WaitingForApproval)
            .Select(r => r.PipelineId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return ids.ToHashSet();
    }
}
