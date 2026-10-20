// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Users;

namespace Aetheus.Back.Components.Auth;

public class RoleService(
    IRoleRepository roleRepo,
    IUserService userService,
    IAuditService auditService,
    IResourceAuthorizationService authorizationService,
    IAdminChangeNotifier notifier,
    IUserChangeNotifier userNotifier,
    IPermissionRepository? permissionRepository = null) : IRoleService
{
    private static readonly string[] ProtectedRoles = ["Admin"];

    /// <summary>The NameIdentifier of the bootstrap identity's token (see <c>AuthService.BuildBootstrapResponse</c>).</summary>
    private const string BootstrapSubject = "bootstrap";

    public async Task<PaginatedResult<RoleDto>> GetRolesAsync(
        PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await roleRepo.GetRolesPagedAsync(
            request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<RoleDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<RoleDto?> GetRoleAsync(int id, CancellationToken ct = default)
    {
        return await roleRepo.GetRoleAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct = default)
    {
        if (await roleRepo.RoleNameExistsAsync(request.Name, ct: ct).ConfigureAwait(false))
            throw new ConflictException($"Role '{request.Name}' already exists.");

        var role = await roleRepo.CreateRoleAsync(request.Name, request.Description, ct).ConfigureAwait(false);

        await auditService.LogAsync("Role.Created", "Role", role.Id, $"Role '{request.Name}' created", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, role.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);

        return new RoleDto
        {
            Id = role.Id,
            Name = role.Name,
            Description = role.Description
        };
    }

    public async Task<RoleDto?> UpdateRoleAsync(int id, UpdateRoleRequest request, CancellationToken ct = default)
    {
        var role = await roleRepo.GetRoleEntityAsync(id, ct).ConfigureAwait(false);
        if (role is null) return null;

        if (ProtectedRoles.Contains(role.Name, StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(role.Name, request.Name, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException($"Cannot rename the '{role.Name}' role.");

        if (await roleRepo.RoleNameExistsAsync(request.Name, id, ct).ConfigureAwait(false))
            throw new ConflictException($"Role '{request.Name}' already exists.");

        role.Name = request.Name;
        role.Description = request.Description;
        await roleRepo.UpdateRoleAsync(role, ct).ConfigureAwait(false);

        await auditService.LogAsync("Role.Updated", "Role", id, $"Role '{request.Name}' updated", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, id, EntityChangeOps.Updated, ct).ConfigureAwait(false);

        return await roleRepo.GetRoleAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteRoleAsync(int id, CancellationToken ct = default)
    {
        var role = await roleRepo.GetRoleEntityAsync(id, ct).ConfigureAwait(false);
        if (role is null) return false;

        if (ProtectedRoles.Contains(role.Name, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException($"Cannot delete the '{role.Name}' role.");

        // Capture the members BEFORE the delete so we can invalidate their authz cache and push a
        // realtime refresh - deleting the role strips their permissions and revokes their cached grants.
        var affected = await roleRepo.GetUsersInRoleAsync(id, ct).ConfigureAwait(false);

        await roleRepo.DeleteRoleAsync(role, ct).ConfigureAwait(false);

        foreach (var member in affected)
            authorizationService.InvalidateRoleCache(member.Username);

        await auditService.LogAsync("Role.Deleted", "Role", id, $"Role '{role.Name}' deleted", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, id, EntityChangeOps.Deleted, ct).ConfigureAwait(false);
        foreach (var member in affected)
            await userNotifier.NotifyPermissionsChangedAsync(member.UserId, PermissionChangeReasons.RolePermissions, ct).ConfigureAwait(false);

        return true;
    }

    public async Task<List<ResourcePermissionDto>> GetPermissionsForRoleAsync(int roleId, CancellationToken ct = default)
    {
        return await roleRepo.GetPermissionsForRoleAsync(roleId, ct).ConfigureAwait(false);
    }

    public async Task SetPermissionsForRoleAsync(int roleId, SetResourcePermissionsRequest request, CancellationToken ct = default)
    {
        var role = await roleRepo.GetRoleEntityAsync(roleId, ct).ConfigureAwait(false);
        if (role is null) throw new NotFoundException($"Role {roleId} not found.");

        await roleRepo.SetPermissionsForRoleAsync(roleId, request.Permissions, ct).ConfigureAwait(false);

        // F-12: invalidate the authz cache for every user that holds this role so new permissions
        // (or revocations) take effect on the next request instead of waiting out the cache TTL.
        // The same projection feeds the per-user realtime push (one query, both concerns).
        var affected = await roleRepo.GetUsersInRoleAsync(roleId, ct).ConfigureAwait(false);
        foreach (var member in affected)
            authorizationService.InvalidateRoleCache(member.Username);

        await auditService.LogAsync("Role.PermissionsUpdated", "Role", roleId,
            $"Permissions updated for role '{role.Name}' ({request.Permissions.Count} entries)", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, roleId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        foreach (var member in affected)
            await userNotifier.NotifyPermissionsChangedAsync(member.UserId, PermissionChangeReasons.RolePermissions, ct).ConfigureAwait(false);
    }

    public async Task<RoleDto?> CloneRoleAsync(int id, CancellationToken ct = default)
    {
        var source = await roleRepo.GetRoleAsync(id, ct).ConfigureAwait(false);
        if (source is null) return null;

        var cloneName = $"{source.Name} (Copy)";
        var suffix = 2;
        while (await roleRepo.RoleNameExistsAsync(cloneName, ct: ct).ConfigureAwait(false))
        {
            cloneName = $"{source.Name} (Copy {suffix++})";
        }

        var newRole = await roleRepo.CreateRoleAsync(cloneName, source.Description, ct).ConfigureAwait(false);

        var permissions = await roleRepo.GetPermissionsForRoleAsync(id, ct).ConfigureAwait(false);
        if (permissions.Count > 0)
        {
            var entries = permissions.Select(p => new ResourcePermissionEntry
            {
                ResourceType = p.ResourceType,
                ResourceId = p.ResourceId,
                Permission = p.Permission
            }).ToList();
            await roleRepo.SetPermissionsForRoleAsync(newRole.Id, entries, ct).ConfigureAwait(false);
        }

        await auditService.LogAsync("Role.Cloned", "Role", newRole.Id,
            $"Role '{cloneName}' cloned from '{source.Name}'", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, newRole.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);

        return await roleRepo.GetRoleAsync(newRole.Id, ct).ConfigureAwait(false);
    }

    public async Task<UserPermissionSummaryDto?> GetEffectivePermissionsAsync(int userId, CancellationToken ct = default)
    {
        var user = await userService.GetUserDetailAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return null;

        return await BuildPermissionSummaryAsync(user, ct).ConfigureAwait(false);
    }

    public async Task<UserPermissionSummaryDto?> GetMyPermissionsAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.FindFirstValue(ClaimTypes.NameIdentifier) == BootstrapSubject)
            return await BuildBootstrapPermissionSummaryAsync(principal, ct).ConfigureAwait(false);

        var user = await userService.GetCurrentUserAsync(ct).ConfigureAwait(false);
        if (user is null) return null;

        return await BuildPermissionSummaryAsync(user, ct).ConfigureAwait(false);
    }

    /// <summary>Build summaries for identities without user rows from token roles and their
    /// permissions, without organisation memberships. Never borrow authority from a user row
    /// sharing the same name.</summary>
    private async Task<UserPermissionSummaryDto> BuildBootstrapPermissionSummaryAsync(
        ClaimsPrincipal principal, CancellationToken ct)
    {
        var roles = principal.Identities
            .SelectMany(identity => identity.FindAll(identity.RoleClaimType))
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var effective = roles.Count == 0
            ? []
            : await roleRepo.GetEffectivePermissionsForRolesAsync(roles, ct).ConfigureAwait(false);
        return new UserPermissionSummaryDto
        {
            Username = principal.Identity?.Name ?? string.Empty,
            Roles = roles,
            EffectivePermissions = effective
        };
    }

    private async Task<UserPermissionSummaryDto> BuildPermissionSummaryAsync(UserDto user, CancellationToken ct)
    {
        var effective = await roleRepo.GetEffectivePermissionsAsync(user.Id, ct).ConfigureAwait(false);
        await AppendOrganizationMembershipPermissionsAsync(user.Username, effective, ct).ConfigureAwait(false);
        return new UserPermissionSummaryDto
        {
            UserId = user.Id,
            Username = user.Username,
            Roles = user.Roles,
            EffectivePermissions = effective
        };
    }

    private async Task AppendOrganizationMembershipPermissionsAsync(
        string username,
        List<EffectivePermissionDto> effective,
        CancellationToken ct)
    {
        if (permissionRepository is null) return;
        var organizationIds = await permissionRepository.GetOrganizationIdsForUsernameAsync(username, ct).ConfigureAwait(false);
        foreach (var resourceType in new[] { ResourceType.Project, ResourceType.Server, ResourceType.PipelineTemplate })
        {
            var resourceIds = await permissionRepository
                .GetResourceIdsByOrganizationsAsync(resourceType, organizationIds, ct)
                .ConfigureAwait(false) ?? [];
            foreach (var resourceId in resourceIds)
            {
                if (effective.Any(permission => permission.ResourceType == resourceType
                    && permission.Permission >= Permission.Read
                    && (permission.ResourceId is null || permission.ResourceId == resourceId)))
                    continue;

                effective.Add(new EffectivePermissionDto
                {
                    ResourceType = resourceType,
                    ResourceId = resourceId,
                    Permission = Permission.Read,
                    GrantedByRole = "Organization member"
                });
            }
        }
    }

    public async Task<List<RoleUserDto>> GetUsersInRoleAsync(int roleId, CancellationToken ct = default)
    {
        var role = await roleRepo.GetRoleEntityAsync(roleId, ct).ConfigureAwait(false);
        if (role is null) throw new NotFoundException($"Role {roleId} not found.");
        return await roleRepo.GetUsersInRoleAsync(roleId, ct).ConfigureAwait(false);
    }

    public async Task<PaginatedResult<RoleUserDto>> GetUsersInRoleAsync(
        int roleId, PaginationRequest request, CancellationToken ct = default)
    {
        await EnsureRoleExistsAsync(roleId, ct).ConfigureAwait(false);
        var (page, pageSize) = request.Normalize();
        var (items, total) = await roleRepo.GetUsersInRolePagedAsync(
            roleId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return ToPage(items, total, page, pageSize);
    }

    public async Task<PaginatedResult<RoleUserDto>> GetUsersAvailableForRoleAsync(
        int roleId, PaginationRequest request, CancellationToken ct = default)
    {
        await EnsureRoleExistsAsync(roleId, ct).ConfigureAwait(false);
        var (page, pageSize) = request.Normalize();
        var (items, total) = await roleRepo.GetUsersAvailableForRolePagedAsync(
            roleId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct).ConfigureAwait(false);
        return ToPage(items, total, page, pageSize);
    }

    private async Task EnsureRoleExistsAsync(int roleId, CancellationToken ct)
    {
        if (await roleRepo.GetRoleEntityAsync(roleId, ct).ConfigureAwait(false) is null)
            throw new NotFoundException($"Role {roleId} not found.");
    }

    private static PaginatedResult<RoleUserDto> ToPage(
        List<RoleUserDto> items, int total, int page, int pageSize) => new()
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };

    public async Task AddUserToRoleAsync(int roleId, int userId, CancellationToken ct = default)
    {
        var role = await roleRepo.GetRoleEntityAsync(roleId, ct).ConfigureAwait(false);
        if (role is null) throw new NotFoundException($"Role {roleId} not found.");

        // The Users module owns the security-stamp rotation + authz cache invalidation.
        await userService.AssignRoleAsync(userId, roleId, ct).ConfigureAwait(false);

        await auditService.LogAsync("Role.UserAdded", "Role", roleId,
            $"User {userId} added to role '{role.Name}'", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, roleId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
    }

    public async Task RemoveUserFromRoleAsync(int roleId, int userId, CancellationToken ct = default)
    {
        var role = await roleRepo.GetRoleEntityAsync(roleId, ct).ConfigureAwait(false);
        if (role is null) throw new NotFoundException($"Role {roleId} not found.");

        var removed = await userService.UnassignRoleAsync(userId, roleId, ct).ConfigureAwait(false);
        if (!removed) throw new NotFoundException($"User {userId} does not hold role '{role.Name}'.");

        await auditService.LogAsync("Role.UserRemoved", "Role", roleId,
            $"User {userId} removed from role '{role.Name}'", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, roleId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
    }
}
