// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Organizations;

public class OrganizationRepository(AppDbContext db) : IOrganizationRepository
{
    public async Task<(List<Organization> Items, int Total)> GetPagedAsync(
        string? search, int page, int pageSize, string? sortBy, bool sortDescending, CancellationToken ct,
        IReadOnlyList<GridFilter>? filters = null, IReadOnlyList<int>? organizationIds = null)
    {
        var query = db.Organizations.AsNoTracking().AsQueryable();
        // An organization's administrator lists only the organizations it administers.
        if (organizationIds is not null)
            query = query.Where(o => organizationIds.Contains(o.Id));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(o => EF.Functions.ILike(o.Name, pattern) || EF.Functions.ILike(o.Slug, pattern));
        }
        // The header filters, before the count.
        query = OrganizationListQuery.Columns.ApplyFilters(query, filters);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        // Two collection includes → split query avoids the orgs×members×projects
        // cartesian blow-up (matches GetWithMembersAndProjectsAsync). OrderBy
        // keeps split pagination deterministic.
        query = ApplySort(query, sortBy, sortDescending);
        var items = await query
            .AsSplitQuery()
            .Include(o => o.Members)
            .Include(o => o.Projects)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, total);
    }

    internal static IQueryable<Organization> ApplySort(
        IQueryable<Organization> query, string? sortBy, bool descending) =>
        (sortBy?.Trim().ToLowerInvariant(), descending) switch
        {
            ("slug", false) => query.OrderBy(o => o.Slug).ThenBy(o => o.Id),
            ("slug", true) => query.OrderByDescending(o => o.Slug).ThenBy(o => o.Id),
            ("description", false) => query.OrderBy(o => o.Description).ThenBy(o => o.Name).ThenBy(o => o.Id),
            ("description", true) => query.OrderByDescending(o => o.Description).ThenBy(o => o.Name).ThenBy(o => o.Id),
            ("membercount", false) => query.OrderBy(o => o.Members.Count).ThenBy(o => o.Name).ThenBy(o => o.Id),
            ("membercount", true) => query.OrderByDescending(o => o.Members.Count).ThenBy(o => o.Name).ThenBy(o => o.Id),
            ("projectcount", false) => query.OrderBy(o => o.Projects.Count).ThenBy(o => o.Name).ThenBy(o => o.Id),
            ("projectcount", true) => query.OrderByDescending(o => o.Projects.Count).ThenBy(o => o.Name).ThenBy(o => o.Id),
            ("createdat", false) => query.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id),
            ("createdat", true) => query.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id),
            (_, true) => query.OrderByDescending(o => o.Name).ThenBy(o => o.Id),
            _ => query.OrderBy(o => o.Name).ThenBy(o => o.Id)
        };

    public async Task<List<(int Id, string Name)>> GetNamesAsync(IReadOnlyList<int>? organizationIds, CancellationToken ct)
    {
        var query = db.Organizations.AsNoTracking();
        if (organizationIds is not null)
            query = query.Where(o => organizationIds.Contains(o.Id));
        var rows = await query.OrderBy(o => o.Name).ThenBy(o => o.Id)
            .Select(o => new { o.Id, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(row => (row.Id, row.Name)).ToList();
    }

    public Task<Organization?> GetByIdAsync(int id, CancellationToken ct) =>
        db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Organization?> GetWithMembersAndProjectsAsync(int id, CancellationToken ct) =>
        db.Organizations
            .AsSplitQuery()
            .Include(o => o.Members).ThenInclude(m => m.User)
            .Include(o => o.Projects)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<bool> NameExistsAsync(string name, int? excludeId, CancellationToken ct) =>
        db.Organizations.AnyAsync(o => o.Name == name && (excludeId == null || o.Id != excludeId), ct);

    public Task<bool> SlugExistsAsync(string slug, int? excludeId, CancellationToken ct) =>
        db.Organizations.AnyAsync(o => o.Slug == slug && (excludeId == null || o.Id != excludeId), ct);

    public async Task AddAsync(Organization org, CancellationToken ct)
    {
        await db.Organizations.AddAsync(org, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct).ConfigureAwait(false);
        if (org is null) return false;
        db.Organizations.Remove(org);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Saves; a concurrent request that already added the same member or role (unique index)
    /// makes this one a 409, never a 500.</summary>
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (Aetheus.Back.Services.OrganizationRoleAssignmentRepository.IsUniqueViolation(exception))
        {
            throw new ConflictException("This organization's members changed at the same time; reload and try again.");
        }
    }

    public Task<OrganizationMember?> GetMemberAsync(int organizationId, int userId, CancellationToken ct) =>
        db.OrganizationMembers.FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId, ct);

    public Task<OrganizationMember?> GetMemberByIdAsync(int memberId, CancellationToken ct) =>
        db.OrganizationMembers.Include(m => m.User).FirstOrDefaultAsync(m => m.Id == memberId, ct);

    public async Task AddMemberAsync(OrganizationMember member, CancellationToken ct)
    {
        await db.OrganizationMembers.AddAsync(member, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveMemberAsync(int organizationId, int memberId, CancellationToken ct)
    {
        var member = await db.OrganizationMembers
            .FirstOrDefaultAsync(m => m.Id == memberId && m.OrganizationId == organizationId, ct)
            .ConfigureAwait(false);
        if (member is null) return false;
        db.OrganizationMembers.Remove(member);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // RBAC audit, finding 7: a deactivated account is never found to be added, so it reads exactly like an
    // unknown one.
    public Task<User?> GetUserByIdAsync(int userId, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);

    public async Task<User?> GetUserByLoginAsync(string login, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(login);
        // An e-mail address is matched whatever its case (stored normalized, PLAN-003 P3-2), and wins over a
        // username equal to it: the Organization Admin who types an address means its owner, not whoever
        // registered it as a login.
        var email = UserEmail.Normalize(login);
        var candidates = await db.Users
            .Where(u => u.IsActive && (u.Username == login || (email != null && u.Email == email)))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return candidates.FirstOrDefault(u => email is not null && string.Equals(u.Email, email, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault(u => string.Equals(u.Username, login, StringComparison.Ordinal));
    }

    public Task<int> CountOwnersAsync(int organizationId, CancellationToken ct) =>
        db.OrganizationMembers.CountAsync(
            m => m.OrganizationId == organizationId && m.Role == Aetheus.Shared.Components.Shared.OrganizationRole.Owner, ct);

    public Task<OrganizationMember?> GetMemberWithUserByIdAsync(int memberId, int organizationId, CancellationToken ct) =>
        db.OrganizationMembers
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.Id == memberId && m.OrganizationId == organizationId, ct);

    public Task<int> CountExistingProjectsAsync(List<int> projectIds, CancellationToken ct) =>
        db.Projects.CountAsync(p => projectIds.Contains(p.Id), ct);

    public Task<List<int>> GetProjectOrganizationIdsAsync(List<int> projectIds, CancellationToken ct) =>
        db.Projects
            .AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => p.OrganizationId)
            .Distinct()
            .ToListAsync(ct);

    public async Task AssignProjectsAsync(int organizationId, List<int> projectIds, CancellationToken ct)
    {
        // Every project must belong to exactly one organization (NOT NULL FK), so this
        // operation reassigns the listed projects to the target org. Projects previously
        // attached to this org but missing from the new list keep their current owner -
        // the caller is expected to move them by reassigning them to another org first.
        var toAttach = await db.Projects
            .Where(p => projectIds.Contains(p.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var p in toAttach)
            p.OrganizationId = organizationId;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> GetDefaultOrganizationIdAsync(CancellationToken ct) =>
        await db.Organizations
            .AsNoTracking()
            .Where(o => o.Slug == "aetheus")
            .Select(o => (int?)o.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<List<(Organization Organization, Aetheus.Shared.Components.Shared.OrganizationRole Role)>> GetOrganizationsForUsernameAsync(string username, CancellationToken ct)
    {
        var rows = await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.User != null && m.User.Username == username && m.User.IsActive)
            .Select(m => new { m.Organization, m.Role })
            .OrderBy(x => x.Organization.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(r => (r.Organization, r.Role)).ToList();
    }

    public async Task<List<int>> GetOrganizationIdsForUserAsync(int userId, CancellationToken ct) =>
        await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.OrganizationId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<List<(Organization Organization, Aetheus.Shared.Components.Shared.OrganizationRole Role, int MemberId)>> GetOrganizationsForUserIdAsync(int userId, CancellationToken ct)
    {
        var rows = await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Organization.Name)
            .Select(m => new { m.Organization, m.Role, MemberId = m.Id })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(r => (r.Organization, r.Role, r.MemberId)).ToList();
    }

    public async Task<List<(int UserId, string Username)>> GetMembersForNotificationAsync(int organizationId, CancellationToken ct)
    {
        var rows = await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.User != null)
            .Select(m => new { m.UserId, m.User!.Username })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(r => (r.UserId, r.Username)).ToList();
    }
}
