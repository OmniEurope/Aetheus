// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Query of <see cref="IPipelineRunRetentionRepository"/>, moved unchanged from
/// <c>PipelineLifecycleRepository</c>; the <see cref="IPipelineRepository"/> facade delegates to it.</summary>
internal sealed class PipelineRunRetentionRepository(AppDbContext db) : IPipelineRunRetentionRepository
{
    public async Task<int> DeleteRunsOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            return await RunsOlderThan(cutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var old = await RunsOlderThan(cutoff).ToListAsync(ct).ConfigureAwait(false);
        db.PipelineRuns.RemoveRange(old);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return old.Count;
    }

    /// <summary>
    /// The runs the 90-day run retention may delete. Deleting a run cascades to its artifacts, so a run is
    /// kept while a release that is not archived names it (it published or last recorded that release) or
    /// links one of its artifacts: the release retention (decision D2, <c>ReleaseRetentionRepository</c>)
    /// owns those, keeps the protected ones (production, N-1, N-2, newest, leased, rolled back) and deletes
    /// the others' runs when it archives them. A pending deferred cleanup keeps its run too.
    /// </summary>
    private IQueryable<PipelineRun> RunsOlderThan(DateTime cutoff) =>
        db.PipelineRuns.Where(r => r.StartedAt < cutoff
            && !r.Tasks.Any(task => task.IsDeferredCleanup
                && (task.Status == TaskExecutionStatus.Pending
                    || task.Status == TaskExecutionStatus.Assigned
                    || task.Status == TaskExecutionStatus.Running)))
        .Where(r => !db.Releases.Any(release => release.Status != ReleaseStatus.Archived
                && (release.PipelineRunId == r.Id || release.CreatedByPipelineRunId == r.Id))
            && !r.Artifacts.Any(artifact => artifact.Releases.Any(release => release.Status != ReleaseStatus.Archived)));
}
