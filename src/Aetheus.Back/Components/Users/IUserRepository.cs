// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Users;

public interface IUserRepository
{
    Task<(List<User> Items, int TotalCount)> GetUsersPagedAsync(
        string? search, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Projected variant of <see cref="GetUsersPagedAsync"/> that returns <see cref="UserDto"/>
    /// directly via a <c>.Select()</c> projection, avoiding loading full entity graphs.
    /// </summary>
    Task<(List<UserDto> Items, int TotalCount)> GetUsersPagedProjectedAsync(
        string? search, int page, int pageSize, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<GridFilter>? filters = null);

    Task<User?> GetUserDetailAsync(int id, CancellationToken ct = default);

    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);

    Task<User?> FindUserAsync(int id, CancellationToken ct = default);

    Task AddUserAsync(User user, CancellationToken ct = default);

    Task RemoveUserAsync(User user, CancellationToken ct = default);

    /// <summary>Counts active users belonging to the <c>Admin</c> role (used for last-admin guard).</summary>
    Task<int> CountActiveAdminsAsync(CancellationToken ct = default);

    Task<List<Role>> GetRolesByNamesAsync(List<string> roleNames, CancellationToken ct = default);

    /// <summary>Finds a single role by id (for the role-assignment endpoints); null if missing.</summary>
    Task<Role?> FindRoleAsync(int roleId, CancellationToken ct = default);

    Task<List<string>> GetAllRoleNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks every active personal access token of the user revoked, saved by the next
    /// <see cref="SaveChangesAsync"/>: a password reset by someone else must end the access the previous
    /// holder of the account may still have through a token (audit SITE-SEC-001). Returns how many.
    /// </summary>
    Task<int> RevokePersonalAccessTokensAsync(int userId, DateTime revokedAt, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
