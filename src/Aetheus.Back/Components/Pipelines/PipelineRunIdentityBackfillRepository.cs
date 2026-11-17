// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineRunIdentityBackfillRepository(AppDbContext db) : IPipelineRunIdentityBackfillRepository
{
    private sealed record MissingUid(int Id, DateTime StartedAt);

    /// <summary>
    /// Each row commits on its own, without a transaction around the batch: a run still executing is
    /// locked for no longer than its own update, and a batch cut in the middle keeps what it filled.
    /// </summary>
    public async Task<int?> FillNextBatchAsync(int afterId, int batchSize, CancellationToken ct = default)
    {
        // Bulk updates do not exist on InMemory, and nothing there needs a backfill.
        if (!db.Database.IsRelational()) return null;

        var rows = await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id > afterId && run.Uid == null)
            .OrderBy(run => run.Id)
            .Take(batchSize)
            .Select(run => new MissingUid(run.Id, run.StartedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (rows.Count == 0) return null;

        foreach (var row in rows)
        {
            Guid? uid = PipelineRunUid.From(row.StartedAt);
            await db.PipelineRuns.Where(run => run.Id == row.Id && run.Uid == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.Uid, uid), ct)
                .ConfigureAwait(false);
        }

        return rows[^1].Id;
    }

    public async Task<PipelineRunIdentityBackfillDto> GetProgressAsync(CancellationToken ct = default) => new()
    {
        Total = await db.PipelineRuns.CountAsync(ct).ConfigureAwait(false),
        Remaining = await db.PipelineRuns.CountAsync(run => run.Uid == null, ct).ConfigureAwait(false)
    };
}
