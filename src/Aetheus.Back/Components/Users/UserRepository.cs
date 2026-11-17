// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Users;

public class UserRepository(AppDbContext db) : IUserRepository
{
    public async Task<(List<User> Items, int TotalCount)> GetUsersPagedAsync(
        string? search, int page, int pageSize, CancellationToken ct = default)
    {
        return await GetUsersPageAsync(
            search, page, pageSize, query => query.IncludeRoles(), ct).ConfigureAwait(false);
    }

    public async Task<(List<UserDto> Items, int TotalCount)> GetUsersPagedProjectedAsync(
        string? search, int page, int pageSize, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<GridFilter>? filters = null, IReadOnlyList<int>? organizationIds = null, bool siteAdminView = true)
    {
        // Limited to organizations (an organization's administrator, or the switcher's choice): their members
        // only, each with the roles it holds there only. Outside the site Admin's view the e-mail is neither
        // returned nor searched, sorted or filtered on, so a caller cannot guess an address by prefix.
        return await GetUsersPageAsync(
            search,
            page,
            pageSize,
            query => query.Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = siteAdminView ? u.Email : null,
                IsActive = u.IsActive,
                TotpEnabled = u.TotpEnabled,
                Roles = u.UserRoles.Where(ur => ur.Role.Name == RoleNames.Admin).Select(ur => ur.Role.Name).ToList(),
                OrganizationRoles = u.OrganizationRoles
                    .Where(assignment => organizationIds == null || organizationIds.Contains(assignment.OrganizationId))
                    .OrderBy(assignment => assignment.Organization.Name).ThenBy(assignment => assignment.Role.Name)
                    .Select(assignment => new OrganizationRoleAssignmentDto
                    {
                        OrganizationId = assignment.OrganizationId,
                        OrganizationName = assignment.Organization.Name,
                        Role = assignment.Role.Name
                    }).ToList(),
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
                LastActivityAt = u.LastActivityAt
            })
            // Two collections per user (the administrator role, the organization roles): one query each,
            // as IncludeRoles does, rather than one join that repeats a user for every pair.
            .AsSplitQuery(),
            ct,
            sortBy,
            sortDescending,
            filters,
            organizationIds,
            siteAdminView).ConfigureAwait(false);
    }

    private async Task<(List<T> Items, int TotalCount)> GetUsersPageAsync<T>(
        string? search,
        int page,
        int pageSize,
        Func<IQueryable<User>, IQueryable<T>> project,
        CancellationToken ct,
        string? sortBy = null,
        bool sortDescending = false,
        IReadOnlyList<GridFilter>? filters = null,
        IReadOnlyList<int>? organizationIds = null,
        bool siteAdminView = true)
    {
        var scoped = BuildUserQuery(search, matchEmail: siteAdminView);
        if (organizationIds is not null)
            scoped = scoped.Where(user => db.OrganizationMembers.Any(member =>
                member.UserId == user.Id && organizationIds.Contains(member.OrganizationId)));
        // The header filters, before the count. A list limited to organizations matches the roles held there only.
        var columns = organizationIds is null && siteAdminView
            ? UserListQuery.Columns
            : UserListQuery.For(organizationIds, siteAdminView);
        var query = columns.ApplyFilters(scoped, siteAdminView ? filters : UserListQuery.WithoutEmail(filters));
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var pageQuery = query
            .OrderByProperty(UserListQuery.SortKey(sortBy, siteAdminView), sortDescending, user => user.Username, fallbackDescending: false)
            .Skip((page - 1) * pageSize)
            .Take(pageSize);
        var items = await project(pageQuery).ToListAsync(ct).ConfigureAwait(false);
        return (items, totalCount);
    }

    private IQueryable<User> BuildUserQuery(string? search, bool matchEmail)
    {
        var query = db.Users.AsNoTracking().AsQueryable();
        if (string.IsNullOrWhiteSpace(search)) return query;
        return matchEmail
            ? query.Where(user => user.Username.Contains(search) || user.Email.Contains(search))
            : query.Where(user => user.Username.Contains(search));
    }

    public async Task<User?> GetUserDetailAsync(int id, CancellationToken ct = default)
    {
        return await db.Users
            .Where(u => u.Id == id)
            .IncludeRoles()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        return await db.Users.FindByUsernameWithRolesAsync(username, ct).ConfigureAwait(false);
    }

    public async Task<bool> EmailInUseAsync(string normalizedEmail, int? exceptUserId, CancellationToken ct = default)
    {
        return await db.Users.AnyAsync(u => u.Email == normalizedEmail && u.Id != exceptUserId, ct).ConfigureAwait(false);
    }

    public async Task<User?> FindUserAsync(int id, CancellationToken ct = default)
    {
        return await db.Users.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task AddUserAsync(User user, CancellationToken ct = default)
    {
        db.Users.Add(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveUserAsync(User user, CancellationToken ct = default)
    {
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<int> CountActiveAdminsAsync(CancellationToken ct = default)
    {
        return db.Users
            .AsNoTracking()
            .Where(u => u.IsActive && u.UserRoles.Any(ur => ur.Role.Name == "Admin"))
            .CountAsync(ct);
    }

    public async Task<List<Role>> GetRolesByNamesAsync(List<string> roleNames, CancellationToken ct = default)
    {
        // Site roles only (R-2): an organization's own role may share a name with another organization's.
        return await db.Roles
            .Where(r => r.OrganizationId == null && roleNames.Contains(r.Name))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Role?> FindRoleAsync(int roleId, CancellationToken ct = default)
    {
        return await db.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roleId, ct)
            .ConfigureAwait(false);
    }

    public async Task<List<string>> GetAllRoleNamesAsync(CancellationToken ct = default)
    {
        return await db.Roles
            .AsNoTracking()
            .Select(r => r.Name)
            // Two organizations may name a role alike (R-2): each name once.
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> RevokePersonalAccessTokensAsync(int userId, DateTime revokedAt, CancellationToken ct = default)
    {
        var tokens = await db.PersonalAccessTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var token in tokens)
            token.RevokedAt = revokedAt;
        return tokens.Count;
    }

    public async Task<int> RevokeRefreshTokensAsync(int userId, DateTime revokedAt, CancellationToken ct = default)
    {
        var tokens = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > revokedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var token in tokens)
            token.RevokedAt = revokedAt;
        return tokens.Count;
    }

    public Task<string[]> GetOwnedPipelineNamesAsync(string username, CancellationToken ct = default) =>
        db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.CreatedByUsername == username)
            .OrderBy(pipeline => pipeline.Name)
            .Select(pipeline => pipeline.Name)
            .ToArrayAsync(ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
