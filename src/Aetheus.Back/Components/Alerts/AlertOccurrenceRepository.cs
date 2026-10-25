// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Alerts;

public sealed class AlertOccurrenceRepository(AppDbContext db) : IAlertOccurrenceRepository
{
    public async Task AddAsync(AlertOccurrence occurrence, CancellationToken ct = default)
    {
        db.AlertOccurrences.Add(occurrence);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // A row that could not be stored must not stay pending in the shared context, or every later
            // SaveChanges of the same scope (the next rule of the evaluation pass) would retry it and fail too.
            db.Entry(occurrence).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<AlertOccurrence?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        return await db.AlertOccurrences
            .AsNoTracking()
            .Where(occurrence => occurrence.Id == id)
            .Include(occurrence => occurrence.AlertRule)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteTriggeredBeforeAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            return await db.AlertOccurrences.Where(o => o.TriggeredAt < cutoffUtc)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var expired = await db.AlertOccurrences.Where(o => o.TriggeredAt < cutoffUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        db.AlertOccurrences.RemoveRange(expired);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return expired.Count;
    }
}
