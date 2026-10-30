// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Auth;

/// <summary>The set-password tokens (R-14) and the account whose password one of them sets.</summary>
public interface IPasswordSetupRepository
{
    Task AddAsync(PasswordSetupToken token, CancellationToken ct = default);

    /// <summary>
    /// Spends the token with that hash if it is unused, unexpired and its account active, in one conditional
    /// write so two simultaneous uses cannot both win. Returns the account's id, or null when refused.
    /// </summary>
    Task<int?> TryClaimAsync(string tokenHash, DateTime now, CancellationToken ct = default);

    /// <summary>Voids every unused token of the account.</summary>
    Task VoidOpenAsync(int userId, DateTime now, CancellationToken ct = default);

    /// <summary>How many links of that purpose the account was sent since <paramref name="since"/>.</summary>
    Task<int> CountIssuedSinceAsync(int userId, PasswordSetupPurpose purpose, DateTime since, CancellationToken ct = default);

    /// <summary>The active account that signs in with that (already normalized) address, untracked.</summary>
    Task<User?> FindActiveByEmailAsync(string normalizedEmail, CancellationToken ct = default);

    /// <summary>The account, untracked.</summary>
    Task<User?> FindUserAsync(int userId, CancellationToken ct = default);

    Task<User?> FindUserTrackedAsync(int userId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class PasswordSetupRepository(AppDbContext db) : IPasswordSetupRepository
{
    public async Task AddAsync(PasswordSetupToken token, CancellationToken ct = default)
    {
        db.PasswordSetupTokens.Add(token);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> TryClaimAsync(string tokenHash, DateTime now, CancellationToken ct = default)
    {
        var candidate = await db.PasswordSetupTokens.AsNoTracking()
            .Where(t => t.TokenHash == tokenHash && t.UsedAt == null && t.ExpiresAt > now && t.User.IsActive)
            .Select(t => new { t.Id, t.UserId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (candidate is null) return null;

        if (db.Database.IsRelational())
        {
            // The condition is re-checked by the write itself: of two simultaneous uses, one updates the row.
            var claimed = await db.PasswordSetupTokens
                .Where(t => t.Id == candidate.Id && t.UsedAt == null && t.ExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.UsedAt, now), ct).ConfigureAwait(false);
            return claimed == 1 ? candidate.UserId : null;
        }
        else
        {
            // InMemory (unit tests): no conditional bulk write; one tracked update.
            var row = await db.PasswordSetupTokens.FirstAsync(t => t.Id == candidate.Id, ct).ConfigureAwait(false);
            if (row.UsedAt is not null) return null;
            row.UsedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return candidate.UserId;
        }
    }

    public async Task VoidOpenAsync(int userId, DateTime now, CancellationToken ct = default)
    {
        var open = await db.PasswordSetupTokens
            .Where(t => t.UserId == userId && t.UsedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var token in open)
            token.UsedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<int> CountIssuedSinceAsync(int userId, PasswordSetupPurpose purpose, DateTime since, CancellationToken ct = default) =>
        db.PasswordSetupTokens.CountAsync(t => t.UserId == userId && t.Purpose == purpose && t.CreatedAt > since, ct);

    public Task<User?> FindActiveByEmailAsync(string normalizedEmail, CancellationToken ct = default) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.IsActive, ct);

    public Task<User?> FindUserAsync(int userId, CancellationToken ct = default) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);

    public Task<User?> FindUserTrackedAsync(int userId, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
