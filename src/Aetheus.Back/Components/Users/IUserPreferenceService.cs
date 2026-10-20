// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Users;

/// <summary>The preferences of one user (recette R1-66); the caller passes the signed-in user's id, never another.</summary>
public interface IUserPreferenceService
{
    Task<List<UserPreferenceDto>> GetAllAsync(int userId, DateTimeOffset? since, CancellationToken ct = default);

    /// <summary>The preference, its tombstone when it was removed, or null when the user never had it.</summary>
    Task<UserPreferenceDto?> GetAsync(int userId, string key, CancellationToken ct = default);

    /// <summary>Stores the value unless the stored one is later (the server wins), and returns what is stored.</summary>
    Task<UserPreferenceDto> SaveAsync(int userId, string key, SaveUserPreferenceRequest request, CancellationToken ct = default);

    /// <summary>Removes the preference by writing its tombstone, dated now.</summary>
    Task RemoveAsync(int userId, string key, CancellationToken ct = default);
}
