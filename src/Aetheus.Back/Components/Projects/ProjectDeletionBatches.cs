// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Projects;

/// <summary>Set-based deletion helpers shared by the project deletion steps. Large tables are deleted
/// <see cref="BatchSize"/> keys at a time, so the size of one statement is bounded whatever the
/// history of the project.</summary>
internal static class ProjectDeletionBatches
{
    internal const int BatchSize = 5_000;

    internal static async Task<int> DeleteAllAsync<T>(AppDbContext db, IQueryable<T> rows, CancellationToken ct)
        where T : class
    {
        if (db.Database.IsRelational())
            return await rows.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // InMemory (unit tests) has no set-based delete.
        var tracked = await rows.ToListAsync(ct).ConfigureAwait(false);
        db.RemoveRange(tracked);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tracked.Count;
    }

    internal static async Task<int> DeleteInBatchesAsync<T, TKey>(
        AppDbContext db, IQueryable<T> rows, Expression<Func<T, TKey>> key, CancellationToken ct)
        where T : class
        where TKey : struct
    {
        var batch = new KeyBatch<TKey>();
        // The keys are read through a member of a captured object, so EF sends them as one parameter
        // and reuses the same statement for every batch.
        var inBatch = Expression.Lambda<Func<T, bool>>(
            Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Contains), [typeof(TKey)],
                Expression.Property(Expression.Constant(batch), nameof(KeyBatch<TKey>.Keys)),
                key.Body),
            key.Parameters);
        var total = 0;
        while (true)
        {
            batch.Keys = await rows.OrderBy(key).Select(key).Take(BatchSize).ToListAsync(ct).ConfigureAwait(false);
            if (batch.Keys.Count == 0) return total;
            total += await DeleteAllAsync(db, rows.Where(inBatch), ct).ConfigureAwait(false);
        }
    }

    private sealed class KeyBatch<TKey>
    {
        public List<TKey> Keys { get; set; } = [];
    }
}
