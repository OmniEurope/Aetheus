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
        IReadOnlyList<GridFilter>? filters = null, IReadOnlyList<int>? organizationIds = null);

    Task<User?> GetUserDetailAsync(int id, CancellationToken ct = default);

    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>True when an account other than <paramref name="exceptUserId"/> already signs in with
    /// <paramref name="normalizedEmail"/> (PLAN-003 P3-2).</summary>
    Task<bool> EmailInUseAsync(string normalizedEmail, int? exceptUserId, CancellationToken ct = default);

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

    /// <summary>
    /// Audit AUD-01 (recette R1-50): marks every refresh token of the user still valid as revoked, in
    /// the tracked context (saved by the next <see cref="SaveChangesAsync"/>). A rotated security stamp
    /// refuses the access tokens, but a refresh token is checked against its own row and would mint a
    /// fresh access token carrying the new stamp. Returns how many were revoked.
    /// </summary>
    Task<int> RevokeRefreshTokensAsync(int userId, DateTime revokedAt, CancellationToken ct = default);

    /// <summary>
    /// Audit AUD-05 (recette R1-50): serializes every change that can remove an active administrator
    /// (role removal, deactivation, deletion) across requests and backend colours, so the "last active
    /// administrator" count is read after the previous such change has committed. Dispose to release.
    /// A provider without transactions (InMemory unit tests) gets a lock that does nothing.
    /// </summary>
    Task<IAsyncDisposable> LockAdministratorSetAsync(CancellationToken ct = default);

    /// <summary>
    /// Names of the pipelines whose automated-run owner (<c>Pipeline.CreatedByUsername</c>) is
    /// <paramref name="username"/>, sorted. An own-read over the shared entity: deactivating or deleting
    /// that user would make the F-EXEC-1b gate refuse every chained run of these pipelines.
    /// </summary>
    Task<string[]> GetOwnedPipelineNamesAsync(string username, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
