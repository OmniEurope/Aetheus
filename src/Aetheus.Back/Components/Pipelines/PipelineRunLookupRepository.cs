// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>Implementation of <see cref="IPipelineRunLookupReader"/> (the run status query moved unchanged
/// from <c>PipelineLifecycleRepository</c>); the <see cref="IPipelineRepository"/> facade delegates to
/// the same queries.</summary>
internal sealed class PipelineRunLookupRepository(AppDbContext db) : IPipelineRunLookupReader
{
    public async Task<int?> GetPipelineIdForRunAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => (int?)r.PipelineId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<int, PipelineStatus>> GetRunStatusesByIdsAsync(IReadOnlyCollection<int> runIds, CancellationToken ct = default)
        => await db.PipelineRuns
            .AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Status })
            .ToDictionaryAsync(x => x.Id, x => x.Status, ct).ConfigureAwait(false);
}
