// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PersonalAccessTokens;

internal sealed class PersonalAccessTokenRepository(AppDbContext db) : IPersonalAccessTokenRepository
{
    public void Add(PersonalAccessToken token) => db.PersonalAccessTokens.Add(token);

    public async Task<PersonalAccessToken?> FindActiveByHashWithUserAsync(string tokenHash, DateTime now, CancellationToken ct = default)
        => await db.PersonalAccessTokens
            .Include(t => t.User)
                .ThenInclude(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.TokenHash == tokenHash
                     && t.RevokedAt == null
                     && t.ExpiresAt > now
                     && t.User.IsActive,
                ct)
            .ConfigureAwait(false);

    public async Task<List<PersonalAccessToken>> GetForUserAsync(int userId, CancellationToken ct = default)
        => await db.PersonalAccessTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<(List<PersonalAccessToken> Items, int Total)> GetForUserPagedAsync(
        int userId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.PersonalAccessTokens
            .AsNoTracking()
            .Where(token => token.UserId == userId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(token =>
                EF.Functions.ILike(token.Name, pattern) ||
                EF.Functions.ILike(token.TokenPrefix, pattern));
        }

        // The header filters, after the owner scope and before the count.
        query = PersonalAccessTokenListQuery.Columns.ApplyFilters(query, columnFilters);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("name", false) => query.OrderBy(token => token.Name).ThenBy(token => token.Id),
            ("name", true) => query.OrderByDescending(token => token.Name).ThenBy(token => token.Id),
            ("expiresat", false) => query.OrderBy(token => token.ExpiresAt).ThenBy(token => token.Id),
            ("expiresat", true) => query.OrderByDescending(token => token.ExpiresAt).ThenBy(token => token.Id),
            ("lastusedat", false) => query.OrderBy(token => token.LastUsedAt).ThenBy(token => token.Id),
            ("lastusedat", true) => query.OrderByDescending(token => token.LastUsedAt).ThenBy(token => token.Id),
            ("createdat", false) => query.OrderBy(token => token.CreatedAt).ThenBy(token => token.Id),
            _ => query.OrderByDescending(token => token.CreatedAt).ThenBy(token => token.Id)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public async Task<PersonalAccessToken?> FindByIdForUserAsync(int id, int userId, CancellationToken ct = default)
        => await db.PersonalAccessTokens
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct)
            .ConfigureAwait(false);

    public async Task TouchLastUsedAsync(int id, DateTime now, CancellationToken ct = default)
    {
        // Bulk update avoids loading the row on the hot auth path. The InMemory provider used by unit
        // tests cannot run ExecuteUpdateAsync, so fall back to a tracked update there.
        if (db.Database.IsRelational())
        {
            await db.PersonalAccessTokens
                .Where(t => t.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct)
                .ConfigureAwait(false);
        }
        else
        {
            var token = await db.PersonalAccessTokens.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
            if (token is not null)
            {
                token.LastUsedAt = now;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await db.SaveChangesAsync(ct).ConfigureAwait(false);
}
