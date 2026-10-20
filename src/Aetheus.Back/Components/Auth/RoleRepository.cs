// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Auth;

public class RoleRepository(AppDbContext db) : IRoleRepository
{
    public async Task<(List<RoleDto> Items, int Total)> GetRolesPagedAsync(
        string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = db.Roles
            .AsNoTracking()
            .Select(r => new RoleDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                PermissionCount = r.ResourcePermissions.Count,
                UserCount = r.UserRoles.Count
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

    public async Task<RoleDto?> GetRoleAsync(int id, CancellationToken ct = default)
    {
        return await db.Roles
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new RoleDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                PermissionCount = r.ResourcePermissions.Count,
                UserCount = r.UserRoles.Count,
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
    }

    public async Task<Role?> GetRoleEntityAsync(int id, CancellationToken ct = default)
    {
        return await db.Roles.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task<bool> RoleNameExistsAsync(string name, int? excludeId = null, CancellationToken ct = default)
    {
        return await db.Roles
            .AsNoTracking()
            .AnyAsync(r => r.Name == name && (excludeId == null || r.Id != excludeId), ct)
            .ConfigureAwait(false);
    }

    public async Task<Role> CreateRoleAsync(string name, string description, CancellationToken ct = default)
    {
        var role = new Role { Name = name, Description = description };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return role;
    }

    public async Task UpdateRoleAsync(Role role, CancellationToken ct = default)
    {
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
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<EffectivePermissionDto>> GetEffectivePermissionsAsync(int userId, CancellationToken ct = default)
    {
        return await (
            from ur in db.UserRoles.AsNoTracking()
            join role in db.Roles.AsNoTracking() on ur.RoleId equals role.Id
            join rp in db.ResourcePermissions.AsNoTracking() on role.Id equals rp.RoleId
            where ur.UserId == userId
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

    public async Task<List<EffectivePermissionDto>> GetEffectivePermissionsForRolesAsync(
        IReadOnlyCollection<string> roleNames, CancellationToken ct = default)
    {
        return await (
            from role in db.Roles.AsNoTracking()
            join rp in db.ResourcePermissions.AsNoTracking() on role.Id equals rp.RoleId
            where roleNames.Contains(role.Name)
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
            .SelectMany(u => u.UserRoles)
            .SelectMany(ur => ur.Role.ResourcePermissions)
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
        return await db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.RoleId == roleId)
            .Select(ur => ur.User.Username)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<RoleUserDto>> GetUsersInRoleAsync(int roleId, CancellationToken ct = default)
    {
        return await db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.RoleId == roleId)
            .OrderBy(ur => ur.User.Username)
            .Select(ur => new RoleUserDto(ur.User.Id, ur.User.Username, ur.User.Email, ur.User.IsActive))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<(List<RoleUserDto> Items, int Total)> GetUsersInRolePagedAsync(
        int roleId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        // SITE-FUNC-ROLE-001: filtered, searched and sorted on the User entity, projected to the
        // positional RoleUserDto only after paging: Npgsql cannot translate a predicate or an
        // ordering on the members of a constructor projection.
        var query = db.Users
            .AsNoTracking()
            .Where(user => user.UserRoles.Any(userRole => userRole.RoleId == roleId));
        // The header filters, before PageUsersAsync counts the page.
        query = RoleListQuery.UserColumns.ApplyFilters(query, filters);
        return PageUsersAsync(query, search, page, pageSize, sortBy, sortDescending, ct);
    }

    public Task<(List<RoleUserDto> Items, int Total)> GetUsersAvailableForRolePagedAsync(
        int roleId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default)
    {
        var query = db.Users
            .AsNoTracking()
            .Where(user => !user.UserRoles.Any(userRole => userRole.RoleId == roleId));
        return PageUsersAsync(query, search, page, pageSize, sortBy, sortDescending, ct);
    }

    private static async Task<(List<RoleUserDto> Items, int Total)> PageUsersAsync(
        IQueryable<User> query, string? search, int page, int pageSize,
        string? sortBy, bool sortDescending, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(user =>
                EF.Functions.ILike(user.Username, pattern) ||
                (user.Email != null && EF.Functions.ILike(user.Email, pattern)));
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
