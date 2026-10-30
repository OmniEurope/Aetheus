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
    IUserChangeNotifier userNotifier) : IRoleService
{
    // ADR-059: the organization model gives these roles by name (R-45), so none is renamed or deleted.
    private static readonly IReadOnlyList<string> ProtectedRoles = RoleNames.BuiltIn;

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

    public async Task<PaginatedResult<RoleDto>> GetRolesAsync(
        PaginationRequest request, IReadOnlyList<int> organizationIds, IReadOnlyList<string>? siteRoleNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(organizationIds);
        var (page, pageSize) = request.Normalize();
        var (items, total) = await roleRepo.GetRolesPagedAsync(
            request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters,
            organizationIds, siteRoleNames).ConfigureAwait(false);
        return new PaginatedResult<RoleDto> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<RoleDto?> GetRoleAsync(int id, CancellationToken ct = default)
    {
        return await roleRepo.GetRoleAsync(id, ct).ConfigureAwait(false);
    }

    public Task<RoleDto?> GetRoleAsync(int id, IReadOnlyList<int>? organizationIds, CancellationToken ct = default) =>
        roleRepo.GetRoleAsync(id, ct, organizationIds);

    public async Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await roleRepo.RoleNameTakenAsync(request.Name, request.OrganizationId, ct: ct).ConfigureAwait(false))
            throw new ConflictException($"Role '{request.Name}' already exists.");
        if (request.OrganizationId is { } organization
            && !await roleRepo.OrganizationExistsAsync(organization, ct).ConfigureAwait(false))
            throw new NotFoundException($"Organization {organization} not found.");

        var keys = ValidatedKeys(request.PermissionKeys ?? []);
        var role = await roleRepo.CreateRoleAsync(request.Name, request.Description, ct, request.OrganizationId).ConfigureAwait(false);
        if (keys.Count > 0)
            await roleRepo.SetPermissionKeysForRoleAsync(role.Id, keys, ct).ConfigureAwait(false);

        await auditService.LogAsync("Role.Created", "Role", role.Id,
            keys.Count > 0
                ? $"Role '{request.Name}' created with permissions: {string.Join(", ", keys)}"
                : $"Role '{request.Name}' created", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, role.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);

        return new RoleDto
        {
            Id = role.Id,
            Name = role.Name,
            Description = role.Description,
            CreatedAt = role.CreatedAt,
            UpdatedAt = role.UpdatedAt,
            OrganizationId = role.OrganizationId,
            PermissionKeys = keys.Count > 0
                ? await roleRepo.GetPermissionKeysForRoleAsync(role.Id, ct).ConfigureAwait(false)
                : []
        };
    }

    public async Task<List<string>> GetPermissionKeysForRoleAsync(int roleId, CancellationToken ct = default)
    {
        await EnsureRoleExistsAsync(roleId, ct).ConfigureAwait(false);
        return await roleRepo.GetPermissionKeysForRoleAsync(roleId, ct).ConfigureAwait(false);
    }

    public async Task SetPermissionKeysForRoleAsync(int roleId, SetRolePermissionKeysRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var role = await roleRepo.GetRoleEntityAsync(roleId, ct).ConfigureAwait(false);
        if (role is null) throw new NotFoundException($"Role {roleId} not found.");
        // The site Admin holds every permission by definition (ADR-059); its grants are not a setting.
        if (string.Equals(role.Name, RoleNames.Admin, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("The Admin role holds every permission; its permissions cannot be changed.");
        var keys = ValidatedKeys(request.Keys);

        await roleRepo.SetPermissionKeysForRoleAsync(roleId, keys, ct).ConfigureAwait(false);

        // As for the level matrix (F-12): the holders' cached grants go now, not at the cache TTL.
        var affected = await roleRepo.GetUsersInRoleAsync(roleId, ct).ConfigureAwait(false);
        foreach (var member in affected)
            authorizationService.InvalidateRoleCache(member.Username);

        await auditService.LogAsync("Role.PermissionsUpdated", "Role", roleId,
            $"Permissions of role '{role.Name}' set to: {(keys.Count == 0 ? "(none)" : string.Join(", ", keys))}", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, roleId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        foreach (var member in affected)
            await userNotifier.NotifyPermissionsChangedAsync(member.UserId, PermissionChangeReasons.RolePermissions, ct).ConfigureAwait(false);
    }

    /// <summary>The distinct keys of the request; one outside the catalogue is refused (400), never ignored.</summary>
    private static List<string> ValidatedKeys(IEnumerable<string> keys)
    {
        var distinct = keys.Distinct(StringComparer.Ordinal).ToList();
        var unknown = distinct.Where(key => PermissionCatalog.Find(key) is null).ToList();
        if (unknown.Count > 0)
            throw new BadRequestException($"Unknown permission(s): {string.Join(", ", unknown)}.");
        return distinct;
    }

    public async Task<RoleDto?> UpdateRoleAsync(int id, UpdateRoleRequest request, CancellationToken ct = default)
    {
        var role = await roleRepo.GetRoleEntityAsync(id, ct).ConfigureAwait(false);
        if (role is null) return null;

        if (ProtectedRoles.Contains(role.Name, StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(role.Name, request.Name, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException($"Cannot rename the '{role.Name}' role.");

        if (await roleRepo.RoleNameTakenAsync(request.Name, role.OrganizationId, id, ct).ConfigureAwait(false))
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
        if (role.OrganizationId is { } organization)
            await EnsureOwnResourcesAsync(organization, request.Permissions, ct).ConfigureAwait(false);

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

    /// <summary>
    /// An organization's own role grants on that organization's resources only: a grant naming a resource of
    /// another organization (or of none, an agent pool, the Docker platform) would name a resource it does not
    /// administer, and show its name back. Refused (400) before anything changes.
    /// </summary>
    private async Task EnsureOwnResourcesAsync(int organizationId, IEnumerable<ResourcePermissionEntry> entries, CancellationToken ct)
    {
        foreach (var entry in entries)
        {
            if (entry.ResourceType is ResourceType.AgentPool or ResourceType.Docker)
                throw new BadRequestException($"An organization's role cannot grant {entry.ResourceType}: it belongs to no organization.");
            if (entry.ResourceId is { } resourceId
                && await authorizationService.GetResourceOrganizationIdAsync(entry.ResourceType, resourceId, ct).ConfigureAwait(false) != organizationId)
                throw new BadRequestException($"{entry.ResourceType} {resourceId} is not a resource of the role's organization.");
        }
    }

    public async Task<RoleDto?> CloneRoleAsync(int id, CancellationToken ct = default)
    {
        var source = await roleRepo.GetRoleAsync(id, ct).ConfigureAwait(false);
        if (source is null) return null;

        var cloneName = $"{source.Name} (Copy)";
        var suffix = 2;
        while (await roleRepo.RoleNameTakenAsync(cloneName, source.OrganizationId, ct: ct).ConfigureAwait(false))
        {
            cloneName = $"{source.Name} (Copy {suffix++})";
        }

        // An organization's own role is cloned into the same organization.
        var newRole = await roleRepo.CreateRoleAsync(cloneName, source.Description, ct, source.OrganizationId).ConfigureAwait(false);

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
        await roleRepo.CopyActionGrantsAsync(id, newRole.Id, ct).ConfigureAwait(false);

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
            EffectivePermissions = effective,
            // No organization role here: only the levels the token's roles carry, or everything for Admin.
            PermissionKeys = roles.Contains(RoleNames.Admin, StringComparer.Ordinal) ? AllKeys() : LevelKeys(effective)
        };
    }

    /// <summary>R-11: the level keys the levels amount to, each with the scope of the level granting it.</summary>
    private static List<EffectivePermissionKeyDto> LevelKeys(IEnumerable<EffectivePermissionDto> levels) =>
        levels.SelectMany(level => PermissionCatalog.LevelKeysUpTo(level.ResourceType, level.Permission)
                .Select(key => new EffectivePermissionKeyDto(key, level.ResourceId, level.OrganizationId)))
            .Distinct()
            .ToList();

    /// <summary>Every key, site-wide: what the Admin role holds.</summary>
    private static List<EffectivePermissionKeyDto> AllKeys() =>
        PermissionCatalog.All.Select(definition => new EffectivePermissionKeyDto(definition.Key, null, null)).ToList();

    private async Task<UserPermissionSummaryDto> BuildPermissionSummaryAsync(UserDto user, CancellationToken ct)
    {
        // Membership alone grants nothing (ADR-059): every right comes from a role, held site-wide
        // (Admin) or for one organization.
        var effective = await roleRepo.GetEffectivePermissionsAsync(user.Id, ct).ConfigureAwait(false);
        var keys = user.Roles.Contains(RoleNames.Admin, StringComparer.Ordinal)
            ? AllKeys()
            : [.. LevelKeys(effective), .. await roleRepo.GetEffectiveActionKeysAsync(user.Id, ct).ConfigureAwait(false)];
        return new UserPermissionSummaryDto
        {
            UserId = user.Id,
            Username = user.Username,
            Roles = user.Roles,
            EffectivePermissions = effective,
            PermissionKeys = keys
        };
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

    public async Task<PaginatedResult<RoleUserDto>> GetUsersInRoleAsync(
        int roleId, PaginationRequest request, IReadOnlyList<int> organizationIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureRoleExistsAsync(roleId, ct).ConfigureAwait(false);
        var (page, pageSize) = request.Normalize();
        var (items, total) = await roleRepo.GetUsersInRolePagedAsync(
            roleId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters, organizationIds).ConfigureAwait(false);
        return ToPage(items, total, page, pageSize);
    }

    public async Task<PaginatedResult<RoleUserDto>> GetUsersAvailableForRoleAsync(
        int roleId, PaginationRequest request, IReadOnlyList<int> organizationIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureRoleExistsAsync(roleId, ct).ConfigureAwait(false);
        var (page, pageSize) = request.Normalize();
        var (items, total) = await roleRepo.GetUsersAvailableForRolePagedAsync(
            roleId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, organizationIds).ConfigureAwait(false);
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

    public async Task AddUserToRoleAsync(int roleId, int userId, int? organizationId, CancellationToken ct = default)
    {
        var role = await roleRepo.GetRoleEntityAsync(roleId, ct).ConfigureAwait(false);
        if (role is null) throw new NotFoundException($"Role {roleId} not found.");
        // An organization's own role is held for that organization only, whoever gives it.
        if (role.OrganizationId is { } own && organizationId != own)
            throw new BadRequestException($"The '{role.Name}' role belongs to one organization and is held for it only.");

        // The Users module owns the security-stamp rotation + authz cache invalidation.
        await userService.AssignRoleAsync(userId, roleId, organizationId, ct).ConfigureAwait(false);

        await auditService.LogAsync("Role.UserAdded", "Role", roleId,
            $"User {userId} added to role '{role.Name}'", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, roleId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
    }

    public async Task RemoveUserFromRoleAsync(int roleId, int userId, int? organizationId, CancellationToken ct = default)
    {
        var role = await roleRepo.GetRoleEntityAsync(roleId, ct).ConfigureAwait(false);
        if (role is null) throw new NotFoundException($"Role {roleId} not found.");

        var removed = await userService.UnassignRoleAsync(userId, roleId, organizationId, ct).ConfigureAwait(false);
        if (!removed) throw new NotFoundException($"User {userId} does not hold role '{role.Name}'.");

        await auditService.LogAsync("Role.UserRemoved", "Role", roleId,
            $"User {userId} removed from role '{role.Name}'", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Role, roleId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
    }
}
