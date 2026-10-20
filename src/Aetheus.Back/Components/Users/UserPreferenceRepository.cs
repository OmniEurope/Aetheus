// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Users;

public sealed class UserPreferenceRepository(AppDbContext db) : IUserPreferenceRepository
{
    public Task<UserPreference?> FindAsync(int userId, string key, CancellationToken ct = default) =>
        db.UserPreferences.AsNoTracking()
            .FirstOrDefaultAsync(preference => preference.UserId == userId && preference.Key == key, ct);

    public Task<List<UserPreference>> ListAsync(int userId, DateTime? since, CancellationToken ct = default)
    {
        var query = db.UserPreferences.AsNoTracking().Where(preference => preference.UserId == userId);
        if (since is { } from)
            query = query.Where(preference => preference.ChangedAt >= from);
        return query.OrderBy(preference => preference.ChangedAt).ToListAsync(ct);
    }

    public async Task<(int Count, int TotalLength)> GetUsageAsync(int userId, string exceptKey, CancellationToken ct = default)
    {
        var rows = db.UserPreferences.AsNoTracking()
            .Where(preference => preference.UserId == userId && preference.Key != exceptKey);
        var count = await rows.CountAsync(ct).ConfigureAwait(false);
        var totalLength = await rows.SumAsync(preference => preference.Value == null ? 0 : preference.Value.Length, ct).ConfigureAwait(false);
        return (count, totalLength);
    }

    public async Task<UserPreference> UpsertAsync(int userId, string key, string? value, DateTime updatedAt, CancellationToken ct = default)
    {
        var row = await db.UserPreferences
            .FirstOrDefaultAsync(preference => preference.UserId == userId && preference.Key == key, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new UserPreference { UserId = userId, Key = key };
            db.UserPreferences.Add(row);
        }

        row.Value = value;
        row.ChangedAt = updatedAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }
}
