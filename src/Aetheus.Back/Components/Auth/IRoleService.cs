// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Auth;

public interface IRoleService
{
    Task<PaginatedResult<RoleDto>> GetRolesAsync(PaginationRequest request, CancellationToken ct = default);

    /// <summary>The roles of <paramref name="organizationIds"/> and the roles of every organization (only
    /// those named in <paramref name="siteRoleNames"/> when given), holders counted in those organizations.</summary>
    Task<PaginatedResult<RoleDto>> GetRolesAsync(
        PaginationRequest request, IReadOnlyList<int> organizationIds, IReadOnlyList<string>? siteRoleNames, CancellationToken ct = default);
    Task<RoleDto?> GetRoleAsync(int id, CancellationToken ct = default);

    /// <summary>The role, holders counted in <paramref name="organizationIds"/> only when given.</summary>
    Task<RoleDto?> GetRoleAsync(int id, IReadOnlyList<int>? organizationIds, CancellationToken ct = default);

    /// <summary>The holders of the role for <paramref name="organizationIds"/> only.</summary>
    Task<PaginatedResult<RoleUserDto>> GetUsersInRoleAsync(
        int roleId, PaginationRequest request, IReadOnlyList<int> organizationIds, CancellationToken ct = default);

    /// <summary>The members of <paramref name="organizationIds"/> the role may be given to.</summary>
    Task<PaginatedResult<RoleUserDto>> GetUsersAvailableForRoleAsync(
        int roleId, PaginationRequest request, IReadOnlyList<int> organizationIds, CancellationToken ct = default);
    Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct = default);
    Task<RoleDto?> UpdateRoleAsync(int id, UpdateRoleRequest request, CancellationToken ct = default);
    Task<bool> DeleteRoleAsync(int id, CancellationToken ct = default);
    Task<List<ResourcePermissionDto>> GetPermissionsForRoleAsync(int roleId, CancellationToken ct = default);
    Task SetPermissionsForRoleAsync(int roleId, SetResourcePermissionsRequest request, CancellationToken ct = default);
    Task<RoleDto?> CloneRoleAsync(int id, CancellationToken ct = default);

    /// <summary>R-11: the role-wide named permissions of the role (404 when it does not exist).</summary>
    Task<List<string>> GetPermissionKeysForRoleAsync(int roleId, CancellationToken ct = default);

    /// <summary>R-11: replaces the role-wide named permissions; an unknown key or the Admin role is refused (400).</summary>
    Task SetPermissionKeysForRoleAsync(int roleId, SetRolePermissionKeysRequest request, CancellationToken ct = default);
    Task<UserPermissionSummaryDto?> GetEffectivePermissionsAsync(int userId, CancellationToken ct = default);
    Task<UserPermissionSummaryDto?> GetMyPermissionsAsync(System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct = default);

    Task<List<RoleUserDto>> GetUsersInRoleAsync(int roleId, CancellationToken ct = default);
    Task<PaginatedResult<RoleUserDto>> GetUsersInRoleAsync(
        int roleId, PaginationRequest request, CancellationToken ct = default);
    Task<PaginatedResult<RoleUserDto>> GetUsersAvailableForRoleAsync(
        int roleId, PaginationRequest request, CancellationToken ct = default);
    Task AddUserToRoleAsync(int roleId, int userId, int? organizationId, CancellationToken ct = default);
    Task RemoveUserFromRoleAsync(int roleId, int userId, int? organizationId, CancellationToken ct = default);
}
