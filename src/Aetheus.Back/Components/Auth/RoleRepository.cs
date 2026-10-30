// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Auth;

public class RoleRepository(AppDbContext db, TimeProvider? time = null) : IRoleRepository
{
    public async Task<(List<RoleDto> Items, int Total)> GetRolesPagedAsync(
        string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null,
        IReadOnlyList<int>? organizationIds = null, IReadOnlyList<string>? siteRoleNames = null)
    {
        var roles = db.Roles.AsNoTracking();
        // Limited to organizations: their own roles, and the roles of every organization (only the names of
        // siteRoleNames when given, the built-in roles for an organization's administrator).
        if (organizationIds is not null)
            roles = roles.Where(r => (r.OrganizationId != null && organizationIds.Contains(r.OrganizationId.Value))
                || (r.OrganizationId == null && (siteRoleNames == null || siteRoleNames.Contains(r.Name))));
        var query = roles
            .Select(r => new RoleDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                OrganizationId = r.OrganizationId,
                OrganizationName = r.Organization != null ? r.Organization.Name : null,
                PermissionCount = r.ResourcePermissions.Count,
                // Limited to organizations, the holders there only.
                UserCount = organizationIds == null
                    ? (r.Name == RoleNames.Admin ? r.UserRoles.Count : 0) + r.OrganizationAssignments.Count
                    : r.OrganizationAssignments.Count(assignment => organizationIds.Contains(assignment.OrganizationId))
            });
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(role =>
                EF.Functions.ILike(role.Name, pattern) ||
                (role.Description != null && EF.Functions.ILike(role.Description, pattern)));
        }

        // The header filters, before the count.
        query = RoleListQuery.Columns.ApplyFilters(query, filters);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("description", false) => query.OrderBy(role => role.Description).ThenBy(role => role.Name),
            ("description", true) => query.OrderByDescending(role => role.Description).ThenBy(role => role.Name),
            ("createdat", false) => query.OrderBy(role => role.CreatedAt).ThenBy(role => role.Name),
            ("createdat", true) => query.OrderByDescending(role => role.CreatedAt).ThenBy(role => role.Name),
            ("updatedat", false) => query.OrderBy(role => role.UpdatedAt).ThenBy(role => role.Name),
            ("updatedat", true) => query.OrderByDescending(role => role.UpdatedAt).ThenBy(role => role.Name),
            ("permissioncount", false) => query.OrderBy(role => role.PermissionCount).ThenBy(role => role.Name),
            ("permissioncount", true) => query.OrderByDescending(role => role.PermissionCount).ThenBy(role => role.Name),
            ("usercount", false) => query.OrderBy(role => role.UserCount).ThenBy(role => role.Name),
            ("usercount", true) => query.OrderByDescending(role => role.UserCount).ThenBy(role => role.Name),
            (_, true) => query.OrderByDescending(role => role.Name).ThenBy(role => role.Id),
            _ => query.OrderBy(role => role.Name).ThenBy(role => role.Id)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public async Task<RoleDto?> GetRoleAsync(int id, CancellationToken ct = default, IReadOnlyList<int>? organizationIds = null)
    {
        var role = await db.Roles
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new RoleDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                OrganizationId = r.OrganizationId,
                OrganizationName = r.Organization != null ? r.Organization.Name : null,
                PermissionCount = r.ResourcePermissions.Count,
                UserCount = organizationIds == null
                    ? (r.Name == RoleNames.Admin ? r.UserRoles.Count : 0) + r.OrganizationAssignments.Count
                    : r.OrganizationAssignments.Count(assignment => organizationIds.Contains(assignment.OrganizationId)),
                Permissions = r.ResourcePermissions.Select(rp => new ResourcePermissionDto
                {
                    Id = rp.Id,
                    ResourceType = rp.ResourceType,
                    ResourceId = rp.ResourceId,
                    Permission = rp.Permission
                }).ToList()
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return role is null
            ? null
            : role with { PermissionKeys = await GetPermissionKeysForRoleAsync(id, ct).ConfigureAwait(false) };
    }

    public async Task<Role?> GetRoleEntityAsync(int id, CancellationToken ct = default)
    {
        return await db.Roles.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task<bool> RoleNameTakenAsync(string name, int? organizationId, int? excludeId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lowered = name.ToLowerInvariant();
        var others = db.Roles.AsNoTracking().Where(r => excludeId == null || r.Id != excludeId);
        // A site role's name is everybody's (the Admin checks go by name, any case: RoleService.ProtectedRoles).
        if (await others.AnyAsync(r => r.OrganizationId == null && r.Name.ToLower() == lowered, ct).ConfigureAwait(false))
            return true;
        return organizationId is { } organization
            // Within the organization only: another organization's role is never seen.
            ? await others.AnyAsync(r => r.OrganizationId == organization && r.Name == name, ct).ConfigureAwait(false)
            // A new site role would make an organization's own role of that name ambiguous: the site Admin sees every
            // organization's roles, so this reveals nothing to it.
            : await others.AnyAsync(r => r.OrganizationId != null && r.Name.ToLower() == lowered, ct).ConfigureAwait(false);
    }

    public Task<bool> OrganizationExistsAsync(int organizationId, CancellationToken ct = default) =>
        db.Organizations.AsNoTracking().AnyAsync(organization => organization.Id == organizationId, ct);

    private DateTime Now() => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public async Task<Role> CreateRoleAsync(string name, string description, CancellationToken ct = default, int? organizationId = null)
    {
        var now = Now();
        var role = new Role { Name = name, Description = description, CreatedAt = now, UpdatedAt = now, OrganizationId = organizationId };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return role;
    }

    public async Task UpdateRoleAsync(Role role, CancellationToken ct = default)
    {
        role.UpdatedAt = Now();
        db.Roles.Update(role);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteRoleAsync(Role role, CancellationToken ct = default)
    {
        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ResourcePermissionDto>> GetPermissionsForRoleAsync(int roleId, CancellationToken ct = default)
    {
        return await db.ResourcePermissions
            .AsNoTracking()
            .Where(rp => rp.RoleId == roleId)
            .Select(rp => new ResourcePermissionDto
            {
                Id = rp.Id,
                ResourceType = rp.ResourceType,
                ResourceId = rp.ResourceId,
                Permission = rp.Permission
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task SetPermissionsForRoleAsync(int roleId, List<ResourcePermissionEntry> permissions, CancellationToken ct = default)
    {
        var existing = await db.ResourcePermissions
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.ResourcePermissions.RemoveRange(existing);

        var newPermissions = permissions.Select(p => new ResourcePermission
        {
            RoleId = roleId,
            ResourceType = p.ResourceType,
            ResourceId = p.ResourceId,
            Permission = p.Permission
        });

        db.ResourcePermissions.AddRange(newPermissions);
        // The permissions are part of the role: their change is a change of the role (R-18).
        var changedRole = await db.Roles.FirstOrDefaultAsync(role => role.Id == roleId, ct).ConfigureAwait(false);
        if (changedRole is not null) changedRole.UpdatedAt = Now();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<string>> GetPermissionKeysForRoleAsync(int roleId, CancellationToken ct = default)
    {
        var levels = await db.ResourcePermissions
            .AsNoTracking()
            .Where(rp => rp.RoleId == roleId && rp.ResourceId == null)
            .Select(rp => new { rp.ResourceType, rp.Permission })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var actions = await db.RolePermissionGrants
            .AsNoTracking()
            .Where(grant => grant.RoleId == roleId && grant.ResourceId == null)
            .Select(grant => grant.PermissionKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var held = levels
            .SelectMany(level => PermissionCatalog.LevelKeysUpTo(level.ResourceType, level.Permission))
            .Concat(actions)
            .ToHashSet(StringComparer.Ordinal);
        // Catalogue order, so the editor and the API list them the same way; a key no longer in the
        // catalogue grants nothing and is not shown.
        return PermissionCatalog.All.Select(definition => definition.Key).Where(held.Contains).ToList();
    }

    public async Task SetPermissionKeysForRoleAsync(int roleId, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        var definitions = keys.Select(PermissionCatalog.Get).ToList();

        // Levels: one role-wide row per resource type the catalogue offers, at the highest level asked.
        var catalogueTypes = PermissionCatalog.All
            .Where(definition => definition.IsLevel)
            .Select(definition => definition.ResourceType!.Value)
            .Distinct()
            .ToList();
        var levelRows = await db.ResourcePermissions
            .Where(rp => rp.RoleId == roleId && rp.ResourceId == null && catalogueTypes.Contains(rp.ResourceType))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        db.ResourcePermissions.RemoveRange(levelRows);
        db.ResourcePermissions.AddRange(definitions
            .Where(definition => definition.IsLevel)
            .GroupBy(definition => definition.ResourceType!.Value)
            .Select(type => new ResourcePermission
            {
                RoleId = roleId,
                ResourceType = type.Key,
                ResourceId = null,
                Permission = type.Max(definition => definition.Level!.Value)
            }));

        // Actions: the role-wide grants; a grant naming one resource is kept.
        var actionRows = await db.RolePermissionGrants
            .Where(grant => grant.RoleId == roleId && grant.ResourceId == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        db.RolePermissionGrants.RemoveRange(actionRows);
        db.RolePermissionGrants.AddRange(definitions
            .Where(definition => !definition.IsLevel)
            .Select(definition => definition.Key)
            .Distinct(StringComparer.Ordinal)
            .Select(key => new RolePermissionGrant { RoleId = roleId, PermissionKey = key }));

        var changedRole = await db.Roles.FirstOrDefaultAsync(role => role.Id == roleId, ct).ConfigureAwait(false);
        if (changedRole is not null) changedRole.UpdatedAt = Now();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task CopyActionGrantsAsync(int sourceRoleId, int targetRoleId, CancellationToken ct = default)
    {
        var grants = await db.RolePermissionGrants
            .AsNoTracking()
            .Where(grant => grant.RoleId == sourceRoleId)
            .Select(grant => new { grant.PermissionKey, grant.ResourceId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        db.RolePermissionGrants.AddRange(grants.Select(grant => new RolePermissionGrant
        {
            RoleId = targetRoleId,
            PermissionKey = grant.PermissionKey,
            ResourceId = grant.ResourceId
        }));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<EffectivePermissionKeyDto>> GetEffectiveActionKeysAsync(int userId, CancellationToken ct = default)
    {
        // Same scoping as GetEffectivePermissionsAsync: a role held for an organization the user is still a
        // member of. The site Admin's keys are added by the caller (it holds every key).
        return await (
            from assignment in db.UserOrganizationRoles.AsNoTracking()
            join grant in db.RolePermissionGrants.AsNoTracking() on assignment.RoleId equals grant.RoleId
            where assignment.UserId == userId
                && db.OrganizationMembers.Any(member =>
                    member.UserId == assignment.UserId && member.OrganizationId == assignment.OrganizationId)
            select new EffectivePermissionKeyDto(grant.PermissionKey, grant.ResourceId, assignment.OrganizationId))
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<EffectivePermissionDto>> GetEffectivePermissionsAsync(int userId, CancellationToken ct = default)
    {
        // ADR-059: the site-wide Admin grants everything; every other role grants its permissions on
        // the resources of the organization it is held for, while the user is still a member of it.
        var siteWide = await (
            from ur in db.UserRoles.AsNoTracking()
            join role in db.Roles.AsNoTracking() on ur.RoleId equals role.Id
            join rp in db.ResourcePermissions.AsNoTracking() on role.Id equals rp.RoleId
            where ur.UserId == userId && role.Name == RoleNames.Admin
            select new EffectivePermissionDto
            {
                ResourceType = rp.ResourceType,
                ResourceId = rp.ResourceId,
                Permission = rp.Permission,
                GrantedByRole = role.Name
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var perOrganization = await (
            from assignment in db.UserOrganizationRoles.AsNoTracking()
            join rp in db.ResourcePermissions.AsNoTracking() on assignment.RoleId equals rp.RoleId
            where assignment.UserId == userId
                && db.OrganizationMembers.Any(member =>
                    member.UserId == assignment.UserId && member.OrganizationId == assignment.OrganizationId)
            select new EffectivePermissionDto
            {
                ResourceType = rp.ResourceType,
                ResourceId = rp.ResourceId,
                Permission = rp.Permission,
                GrantedByRole = assignment.Role.Name,
                OrganizationId = assignment.OrganizationId,
                OrganizationName = assignment.Organization.Name
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. siteWide, .. perOrganization];
    }

    public async Task<List<EffectivePermissionDto>> GetEffectivePermissionsForRolesAsync(
        IReadOnlyCollection<string> roleNames, CancellationToken ct = default)
    {
        return await (
            from role in db.Roles.AsNoTracking()
            join rp in db.ResourcePermissions.AsNoTracking() on role.Id equals rp.RoleId
            where role.OrganizationId == null && roleNames.Contains(role.Name)
            select new EffectivePermissionDto
            {
                ResourceType = rp.ResourceType,
                ResourceId = rp.ResourceId,
                Permission = rp.Permission,
                GrantedByRole = role.Name
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ResourcePermissionDto>> GetUserPermissionsAsync(string username, CancellationToken ct = default)
    {
        return await db.Users
            .AsNoTracking()
            .Where(u => u.Username == username && u.IsActive)
            .SelectMany(u => u.UserRoles.Where(ur => ur.Role.Name == RoleNames.Admin).Select(ur => ur.Role)
                .Concat(u.OrganizationRoles.Select(assignment => assignment.Role)))
            .SelectMany(role => role.ResourcePermissions)
            .Select(rp => new ResourcePermissionDto
            {
                Id = rp.Id,
                ResourceType = rp.ResourceType,
                ResourceId = rp.ResourceId,
                Permission = rp.Permission
            })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<string>> GetUsernamesInRoleAsync(int roleId, CancellationToken ct = default)
    {
        return await db.Users
            .AsNoTracking()
            .Where(HoldsRole(roleId))
            .Select(user => user.Username)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<RoleUserDto>> GetUsersInRoleAsync(int roleId, CancellationToken ct = default)
    {
        return await db.Users
            .AsNoTracking()
            .Where(HoldsRole(roleId))
            .OrderBy(user => user.Username)
            .Select(user => new RoleUserDto(user.Id, user.Username, user.Email, user.IsActive, null, null))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<(List<RoleUserDto> Items, int Total)> GetUsersInRolePagedAsync(
        int roleId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null, IReadOnlyList<int>? organizationIds = null)
    {
        // SITE-FUNC-ROLE-001: filtered, searched and sorted on the User entity, projected to the
        // positional RoleUserDto only after paging: Npgsql cannot translate a predicate or an
        // ordering on the members of a constructor projection.
        // Limited to organizations: the holders for those organizations only, never a site-wide holder.
        var query = db.Users
            .AsNoTracking()
            .Where(organizationIds is null
                ? HoldsRole(roleId)
                : user => user.OrganizationRoles.Any(assignment =>
                    assignment.RoleId == roleId && organizationIds.Contains(assignment.OrganizationId)));
        // The header filters, before PageUsersAsync counts the page.
        query = RoleListQuery.UserColumns.ApplyFilters(query, filters);
        return PageUsersAsync(query, search, page, pageSize, sortBy, sortDescending, ct);
    }

    public async Task<(List<RoleUserDto> Items, int Total)> GetUsersAvailableForRolePagedAsync(
        int roleId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<int>? organizationIds = null)
    {
        // A role held for one organization can still be given to its holders for another one, so
        // only the site-wide Admin role excludes the users who already hold it (ADR-059).
        var query = db.Users.AsNoTracking();
        // Limited to organizations (an organization's own role): their members only.
        if (organizationIds is not null)
            query = query.Where(user => db.OrganizationMembers.Any(member =>
                member.UserId == user.Id && organizationIds.Contains(member.OrganizationId)));
        if (await db.Roles.AnyAsync(role => role.Id == roleId && role.Name == RoleNames.Admin, ct).ConfigureAwait(false))
            query = query.Where(user => !user.UserRoles.Any(userRole => userRole.RoleId == roleId));
        return await PageUsersAsync(query, search, page, pageSize, sortBy, sortDescending, ct).ConfigureAwait(false);
    }

    /// <summary>Holds the role site-wide (Admin only, ADR-059) or for at least one organization.</summary>
    private static System.Linq.Expressions.Expression<Func<User, bool>> HoldsRole(int roleId) =>
        user => user.UserRoles.Any(userRole => userRole.RoleId == roleId && userRole.Role.Name == RoleNames.Admin)
            || user.OrganizationRoles.Any(assignment => assignment.RoleId == roleId);

    private static async Task<(List<RoleUserDto> Items, int Total)> PageUsersAsync(
        IQueryable<User> query, string? search, int page, int pageSize,
        string? sortBy, bool sortDescending, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(user =>
                EF.Functions.ILike(user.Username, pattern) ||
                EF.Functions.ILike(user.Email, pattern));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("email", false) => query.OrderBy(user => user.Email).ThenBy(user => user.Username),
            ("email", true) => query.OrderByDescending(user => user.Email).ThenBy(user => user.Username),
            ("isactive", false) => query.OrderBy(user => user.IsActive).ThenBy(user => user.Username),
            ("isactive", true) => query.OrderByDescending(user => user.IsActive).ThenBy(user => user.Username),
            (_, true) => query.OrderByDescending(user => user.Username).ThenBy(user => user.Id),
            _ => query.OrderBy(user => user.Username).ThenBy(user => user.Id)
        };
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(user => new RoleUserDto(user.Id, user.Username, user.Email, user.IsActive))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, total);
    }
}
