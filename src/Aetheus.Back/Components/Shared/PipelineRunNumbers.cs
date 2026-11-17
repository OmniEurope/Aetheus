// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Shared;

/// <summary>R-8: reads the number a person sees for runs a row only references by technical key. One small
/// query for a whole page, so a list neither loads the run aggregates (YAML and variable snapshots) nor
/// runs one query per row.</summary>
internal static class PipelineRunNumbers
{
    internal static async Task<IReadOnlyDictionary<int, int>> ReadAsync(
        AppDbContext db, IEnumerable<int> runIds, CancellationToken ct)
    {
        var ids = runIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, int>();
        return await db.PipelineRuns.AsNoTracking()
            .Where(run => ids.Contains(run.Id))
            .ToDictionaryAsync(run => run.Id, run => run.BuildNumber, ct)
            .ConfigureAwait(false);
    }
}
