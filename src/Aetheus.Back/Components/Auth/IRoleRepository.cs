// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Auth;

public interface IRoleRepository
{
    Task<(List<RoleDto> Items, int Total)> GetRolesPagedAsync(
        string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null,
        IReadOnlyList<int>? organizationIds = null, IReadOnlyList<string>? siteRoleNames = null);

    /// <summary>The role; with <paramref name="organizationIds"/>, its holder count counts those organizations only.</summary>
    Task<RoleDto?> GetRoleAsync(int id, CancellationToken ct = default, IReadOnlyList<int>? organizationIds = null);
    Task<Role?> GetRoleEntityAsync(int id, CancellationToken ct = default);
    /// <summary>
    /// R-2: true when <paramref name="name"/> is taken for a role of <paramref name="organizationId"/> (null: a site
    /// role). Taken within that organization only, never by another organization's role; a site role's name is
    /// taken in every organization (any case), and a site role cannot take a name an organization already uses, so
    /// (organization, name) always names one role. <paramref name="excludeId"/> is the role being renamed.
    /// </summary>
    Task<bool> RoleNameTakenAsync(string name, int? organizationId, int? excludeId = null, CancellationToken ct = default);
    Task<Role> CreateRoleAsync(string name, string description, CancellationToken ct = default, int? organizationId = null);

    Task<bool> OrganizationExistsAsync(int organizationId, CancellationToken ct = default);
    Task UpdateRoleAsync(Role role, CancellationToken ct = default);
    Task DeleteRoleAsync(Role role, CancellationToken ct = default);
    Task<List<ResourcePermissionDto>> GetPermissionsForRoleAsync(int roleId, CancellationToken ct = default);
    Task SetPermissionsForRoleAsync(int roleId, List<ResourcePermissionEntry> permissions, CancellationToken ct = default);
    Task<List<EffectivePermissionDto>> GetEffectivePermissionsAsync(int userId, CancellationToken ct = default);

    /// <summary>R-11: the role-wide named permissions of the role, level keys included, in catalogue order.</summary>
    Task<List<string>> GetPermissionKeysForRoleAsync(int roleId, CancellationToken ct = default);

    /// <summary>R-11: replaces the role-wide levels (of the catalogue's resource types) and the role-wide
    /// action grants with <paramref name="keys"/>, all of them catalogue keys; grants naming one resource stay.</summary>
    Task SetPermissionKeysForRoleAsync(int roleId, IReadOnlyCollection<string> keys, CancellationToken ct = default);

    /// <summary>Copies every action grant of a role to another (role clone).</summary>
    Task CopyActionGrantsAsync(int sourceRoleId, int targetRoleId, CancellationToken ct = default);

    /// <summary>R-11: the action keys the user holds through the roles it holds for its organizations.</summary>
    Task<List<EffectivePermissionKeyDto>> GetEffectiveActionKeysAsync(int userId, CancellationToken ct = default);

    /// <summary>The permissions the named roles grant, for an identity without a user
    /// row.</summary>
    Task<List<EffectivePermissionDto>> GetEffectivePermissionsForRolesAsync(
        IReadOnlyCollection<string> roleNames, CancellationToken ct = default);
    Task<List<ResourcePermissionDto>> GetUserPermissionsAsync(string username, CancellationToken ct = default);

    /// <summary>F-12: returns usernames of users that hold the given role (for cache invalidation).</summary>
    Task<List<string>> GetUsernamesInRoleAsync(int roleId, CancellationToken ct = default);

    /// <summary>Returns the users that hold the given role (for the role detail "Users" tab and
    /// for per-user cache invalidation + realtime notification).</summary>
    Task<List<RoleUserDto>> GetUsersInRoleAsync(int roleId, CancellationToken ct = default);
    Task<(List<RoleUserDto> Items, int Total)> GetUsersInRolePagedAsync(
        int roleId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null, IReadOnlyList<int>? organizationIds = null);
    Task<(List<RoleUserDto> Items, int Total)> GetUsersAvailableForRolePagedAsync(
        int roleId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<int>? organizationIds = null);
}
