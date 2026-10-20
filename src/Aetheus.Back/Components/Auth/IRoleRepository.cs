// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Auth;

public interface IRoleRepository
{
    Task<(List<RoleDto> Items, int Total)> GetRolesPagedAsync(
        string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null);
    Task<RoleDto?> GetRoleAsync(int id, CancellationToken ct = default);
    Task<Role?> GetRoleEntityAsync(int id, CancellationToken ct = default);
    Task<bool> RoleNameExistsAsync(string name, int? excludeId = null, CancellationToken ct = default);
    Task<Role> CreateRoleAsync(string name, string description, CancellationToken ct = default);
    Task UpdateRoleAsync(Role role, CancellationToken ct = default);
    Task DeleteRoleAsync(Role role, CancellationToken ct = default);
    Task<List<ResourcePermissionDto>> GetPermissionsForRoleAsync(int roleId, CancellationToken ct = default);
    Task SetPermissionsForRoleAsync(int roleId, List<ResourcePermissionEntry> permissions, CancellationToken ct = default);
    Task<List<EffectivePermissionDto>> GetEffectivePermissionsAsync(int userId, CancellationToken ct = default);

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
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null);
    Task<(List<RoleUserDto> Items, int Total)> GetUsersAvailableForRolePagedAsync(
        int roleId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default);
}
