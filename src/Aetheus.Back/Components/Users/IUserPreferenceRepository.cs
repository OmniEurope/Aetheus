// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Users;

/// <summary>The rows of <see cref="UserPreference"/>, always filtered on the one user given.</summary>
public interface IUserPreferenceRepository
{
    Task<UserPreference?> FindAsync(int userId, string key, CancellationToken ct = default);

    /// <summary>The user's preferences changed at or after <paramref name="since"/> (all of them when null), tombstones included.</summary>
    Task<List<UserPreference>> ListAsync(int userId, DateTime? since, CancellationToken ct = default);

    /// <summary>How many keys the user keeps and the sum of their value lengths, leaving <paramref name="exceptKey"/> out.</summary>
    Task<(int Count, int TotalLength)> GetUsageAsync(int userId, string exceptKey, CancellationToken ct = default);

    /// <summary>Writes the value and the date of <paramref name="key"/> for the user, adding the row when it does not exist.</summary>
    Task<UserPreference> UpsertAsync(int userId, string key, string? value, DateTime updatedAt, CancellationToken ct = default);
}
