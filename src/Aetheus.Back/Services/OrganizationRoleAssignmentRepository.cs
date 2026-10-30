// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Services;

/// <summary>Reads and writes the (user, role, organization) assignments of ADR-059 and the
/// memberships they keep in step with. Every write is saved by the caller through <see cref="SaveChangesAsync"/>.</summary>
public interface IOrganizationRoleAssignmentRepository
{
    /// <summary>
    /// R-2: the role each (organization, name) names in that organization: the organization's own role of that
    /// name, else the site role (no organization) of that name. Another organization's role is never returned,
    /// whatever its name, so two organizations may name their roles alike. A pair naming no role is absent.
    /// </summary>
    Task<Dictionary<(int OrganizationId, string Name), RoleInOrganization>> ResolveRolesAsync(
        IEnumerable<(int OrganizationId, string Name)> wanted, CancellationToken ct);

    /// <summary>The role of that id, or null.</summary>
    Task<RoleInOrganization?> FindRoleAsync(int roleId, CancellationToken ct);
    Task<List<UserOrganizationRole>> GetForUserAsync(int userId, CancellationToken ct);
    Task<List<UserOrganizationRole>> GetForUserInOrganizationAsync(int userId, int organizationId, CancellationToken ct);
    Task<List<OrganizationMember>> GetMembershipsAsync(int userId, CancellationToken ct);
    Task<List<int>> GetExistingOrganizationIdsAsync(IEnumerable<int> organizationIds, CancellationToken ct);
    void Add(UserOrganizationRole assignment);
    void Remove(UserOrganizationRole assignment);
    void AddMember(OrganizationMember member);
    void RemoveMember(OrganizationMember member);
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>How many Owners the organization has, read from the database (not the tracked rows).</summary>
    Task<int> CountOwnersAsync(int organizationId, CancellationToken ct);

    /// <summary>Serializes every change that can remove an organization's Owner until the handle is
    /// disposed, after the caller saved: two such changes never both pass the last-Owner count.</summary>
    Task<IAsyncDisposable> LockOwnersAsync(CancellationToken ct);
}

public sealed class OrganizationRoleAssignmentRepository(AppDbContext db) : IOrganizationRoleAssignmentRepository
{
    public async Task<Dictionary<(int OrganizationId, string Name), RoleInOrganization>> ResolveRolesAsync(
        IEnumerable<(int OrganizationId, string Name)> wanted, CancellationToken ct)
    {
        var pairs = wanted.Distinct().ToList();
        var resolved = new Dictionary<(int OrganizationId, string Name), RoleInOrganization>();
        if (pairs.Count == 0) return resolved;
        var names = pairs.Select(pair => pair.Name).Distinct(StringComparer.Ordinal).ToList();
        var organizations = pairs.Select(pair => pair.OrganizationId).Distinct().ToList();
        var candidates = await db.Roles.AsNoTracking()
            .Where(role => names.Contains(role.Name)
                && (role.OrganizationId == null || organizations.Contains(role.OrganizationId.Value)))
            .Select(role => new RoleInOrganization(role.Id, role.Name, role.OrganizationId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var pair in pairs)
        {
            var role = candidates.FirstOrDefault(item => item.OrganizationId == pair.OrganizationId && string.Equals(item.Name, pair.Name, StringComparison.Ordinal))
                ?? candidates.FirstOrDefault(item => item.OrganizationId == null && string.Equals(item.Name, pair.Name, StringComparison.Ordinal));
            if (role is not null) resolved[pair] = role;
        }
        return resolved;
    }

    public Task<RoleInOrganization?> FindRoleAsync(int roleId, CancellationToken ct) =>
        db.Roles.AsNoTracking()
            .Where(role => role.Id == roleId)
            .Select(role => new RoleInOrganization(role.Id, role.Name, role.OrganizationId))
            .FirstOrDefaultAsync(ct);

    public async Task<List<UserOrganizationRole>> GetForUserAsync(int userId, CancellationToken ct) =>
        await db.UserOrganizationRoles
            .Where(assignment => assignment.UserId == userId)
            .Include(assignment => assignment.Role)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<List<UserOrganizationRole>> GetForUserInOrganizationAsync(int userId, int organizationId, CancellationToken ct) =>
        await db.UserOrganizationRoles
            .Where(assignment => assignment.UserId == userId && assignment.OrganizationId == organizationId)
            .Include(assignment => assignment.Role)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<List<OrganizationMember>> GetMembershipsAsync(int userId, CancellationToken ct) =>
        await db.OrganizationMembers
            .Where(member => member.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<List<int>> GetExistingOrganizationIdsAsync(IEnumerable<int> organizationIds, CancellationToken ct)
    {
        var wanted = organizationIds.Distinct().ToList();
        return await db.Organizations.AsNoTracking()
            .Where(organization => wanted.Contains(organization.Id))
            .Select(organization => organization.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public void Add(UserOrganizationRole assignment) => db.UserOrganizationRoles.Add(assignment);

    public void Remove(UserOrganizationRole assignment) => db.UserOrganizationRoles.Remove(assignment);

    public void AddMember(OrganizationMember member) => db.OrganizationMembers.Add(member);

    public void RemoveMember(OrganizationMember member) => db.OrganizationMembers.Remove(member);

    public Task<int> CountOwnersAsync(int organizationId, CancellationToken ct) =>
        db.OrganizationMembers.AsNoTracking()
            .CountAsync(member => member.OrganizationId == organizationId && member.Role == OrganizationRole.Owner, ct);

    public Task<IAsyncDisposable> LockOwnersAsync(CancellationToken ct) =>
        PostgresSessionLock.AcquireAsync(db, "aetheus:organization-owners", ct);

    /// <summary>Saves; two requests giving the same role at once hit the unique index, which is a 409
    /// for the later one (the state it asked for exists), never a 500.</summary>
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new ConflictException("The roles or memberships of this user changed at the same time; reload and try again.");
        }
    }

    internal static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}

/// <summary>A role as an assignment sees it: its id, its name and its organization (null for a site role).</summary>
public sealed record RoleInOrganization(int Id, string Name, int? OrganizationId);
