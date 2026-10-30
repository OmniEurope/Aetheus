// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Users;

public interface IUserService
{
    Task<PaginatedResult<UserDto>> GetUsersAsync(PaginationRequest request, CancellationToken ct = default);

    /// <summary>
    /// The members of <paramref name="organizationIds"/>, each with its roles there only (Administration
    /// section). <paramref name="siteAdmin"/> false is an organization's administrator: as on the organization
    /// page (RBAC audit, finding 7) it reads neither the members' e-mails nor their site-wide roles.
    /// </summary>
    Task<PaginatedResult<UserDto>> GetOrganizationUsersAsync(
        PaginationRequest request, IReadOnlyList<int> organizationIds, bool siteAdmin, CancellationToken ct = default);
    Task<UserDto?> GetUserDetailAsync(int id, CancellationToken ct = default);
    Task<UserDto?> GetCurrentUserAsync(CancellationToken ct = default);
    Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserDto?> UpdateUserAsync(int id, UpdateUserRequest request, CancellationToken ct = default);
    /// <summary>Deletes the user. a pipeline owner is refused (409) unless <paramref
    /// name="transferPipelinesTo"/> names an active user to take the pipelines over, or <paramref
    /// name="forceWithOwnedPipelines"/> is set.</summary>
    Task<bool> DeleteUserAsync(
        int id, CancellationToken ct = default, string? transferPipelinesTo = null, bool forceWithOwnedPipelines = false);
    Task<bool> ChangePasswordAsync(int id, ChangeUserPasswordRequest request, CancellationToken ct = default);
    Task<bool> ChangeOwnPasswordAsync(ChangeUserPasswordRequest request, CancellationToken ct = default);
    Task<List<string>> GetRolesAsync(CancellationToken ct = default);

    /// <summary>Adds a single role to a user (owns the security-stamp rotation and cache invalidation): Admin
    /// site-wide (no organization), any other role for <paramref name="organizationId"/> (ADR-059).</summary>
    Task AssignRoleAsync(int userId, int roleId, int? organizationId, CancellationToken ct = default);

    /// <summary>Removes a single role from a user; refuses to strip the last active admin of the Admin role.</summary>
    Task<bool> UnassignRoleAsync(int userId, int roleId, int? organizationId, CancellationToken ct = default);
}
