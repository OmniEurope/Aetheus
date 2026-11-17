// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PersonalAccessTokens;

public interface IPersonalAccessTokenRepository
{
    void Add(PersonalAccessToken token);

    /// <summary>Resolves an active (not revoked, not expired, active user) token by its hash, with the
    /// owning user and their current roles loaded so the caller can build a live principal.</summary>
    Task<PersonalAccessToken?> FindActiveByHashWithUserAsync(IReadOnlyCollection<string> tokenHashes, DateTime now, CancellationToken ct = default);

    Task<List<PersonalAccessToken>> GetForUserAsync(int userId, CancellationToken ct = default);

    Task<(List<PersonalAccessToken> Items, int Total)> GetForUserPagedAsync(
        int userId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null);

    Task<PersonalAccessToken?> FindByIdForUserAsync(int id, int userId, CancellationToken ct = default);

    /// <summary>Throttled best-effort write of <c>LastUsedAt</c> (usage trace) without loading the row.</summary>
    Task TouchLastUsedAsync(int id, DateTime now, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
