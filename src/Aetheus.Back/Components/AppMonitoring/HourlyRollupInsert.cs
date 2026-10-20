// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>Hourly rollup insertion keeps existing rows and adds only missing ones through LINQ,
/// tolerating concurrent aggregation.</summary>
internal static class HourlyRollupInsert
{
    /// <summary>Writes tried before a unique violation is let through: each retry drops the rows that are
    /// now present, so only a third writer racing on the same keys again can exhaust them.</summary>
    internal const int MaxAttempts = 3;

    /// <param name="db">The repository's context; nothing else may be pending in it.</param>
    /// <param name="rows">The rollups computed for the hour, none of them present at the time of the read.</param>
    /// <param name="stillMissing">Re-reads the hour after a conflict and tells which rows are still absent.</param>
    /// <returns>The number of rows this call inserted.</returns>
    internal static async Task<int> AddMissingAsync<TRow>(
        AppDbContext db,
        List<TRow> rows,
        Func<CancellationToken, Task<Func<TRow, bool>>> stillMissing,
        CancellationToken ct)
        where TRow : class
    {
        for (var attempt = 1; ; attempt++)
        {
            db.Set<TRow>().AddRange(rows);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return rows.Count;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && AppErrorRepository.IsUniqueViolation(ex))
            {
                // Mandatory catch: another writer inserted some of these keys between the read and the
                // write. SaveChanges is atomic, nothing of this batch went in: the rows leave the context
                // and the ones the other writer did not cover are written again.
                foreach (var row in rows)
                    db.Entry(row).State = EntityState.Detached;
                var missing = await stillMissing(ct).ConfigureAwait(false);
                rows = rows.Where(missing).ToList();
                if (rows.Count == 0)
                    return 0;
            }
        }
    }
}
