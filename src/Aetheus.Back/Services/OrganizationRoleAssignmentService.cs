// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Services;

/// <summary>
/// Keeps roles and memberships in step (ADR-059): a role is always held for one organization, holding
/// one makes the user a member (R-47), and every member holds the role its membership level gives
/// (R-45: Member = Reader, Maintainer = Contributor, Owner = Organization Admin). Adding a member from the
/// organization and giving the same role from the user end in the same rows. The caller invalidates the
/// user's authorization cache and rotates its security stamp. A role named in an assignment is looked up within
/// that assignment's organization (R-2): its own role of that name, else the site role; never another
/// organization's, which may carry the same name.
/// </summary>
public interface IOrganizationRoleAssignmentService
{
    /// <summary>A new member receives the role its membership level gives.</summary>
    Task GrantMembershipRoleAsync(int userId, int organizationId, OrganizationRole membership, CancellationToken ct);

    /// <summary>A membership level change swaps the role the old level gave for the new level's.</summary>
    Task ChangeMembershipRoleAsync(int userId, int organizationId, OrganizationRole previous, OrganizationRole current, CancellationToken ct);

    /// <summary>A member leaving the organization loses every role held for it.</summary>
    Task RemoveAllAsync(int userId, int organizationId, CancellationToken ct);

    /// <summary>
    /// Sets the complete set of roles the user holds per organization: an organization gained makes the
    /// user a member, an organization no longer named removes the membership, and the membership level
    /// follows the highest role held. Returns true when anything changed.
    /// </summary>
    Task<bool> ReplaceForUserAsync(int userId, IReadOnlyCollection<OrganizationRoleAssignmentDto> requested, CancellationToken ct);

    /// <summary>Refuses (400) a set <see cref="ReplaceForUserAsync"/> would refuse, before the caller
    /// changes anything: the site-wide Admin role, an unknown role or an unknown organization.</summary>
    Task ValidateAsync(IReadOnlyCollection<OrganizationRoleAssignmentDto> requested, CancellationToken ct);

    /// <summary>True when every role requested is one an organization's administrator gives for that organization
    /// (R-10 d): a built-in role held for one organization, or that organization's own role. False for the Admin
    /// role, a role of every organization made by the site Admin, another organization's role, an unknown role.</summary>
    Task<bool> AreGivenByOrganizationAdministratorAsync(IReadOnlyCollection<OrganizationRoleAssignmentDto> requested, CancellationToken ct);

    /// <summary>Gives the user the role <paramref name="roleId"/> for the organization, making it a member
    /// when it is not one yet. Returns false when the user already held it.</summary>
    Task<bool> AssignAsync(int userId, int organizationId, int roleId, CancellationToken ct);

    /// <summary>Takes the role <paramref name="roleId"/> for the organization from the user; the last role
    /// removed ends the membership. Returns false when the user did not hold it.</summary>
    Task<bool> UnassignAsync(int userId, int organizationId, int roleId, CancellationToken ct);

    /// <summary>Refuses (409) a set that would take the Owner level from the last Owner of an
    /// organization, before the caller changes anything. <see cref="ReplaceForUserAsync"/> checks it
    /// again under <see cref="LockOwnersAsync"/>.</summary>
    Task RefuseLastOwnerLossAsync(int userId, IReadOnlyCollection<OrganizationRoleAssignmentDto> requested, CancellationToken ct);

    /// <summary>Serializes the changes that can remove an organization's Owner; hold it until saved.</summary>
    Task<IAsyncDisposable> LockOwnersAsync(CancellationToken ct);
}

public sealed class OrganizationRoleAssignmentService(IOrganizationRoleAssignmentRepository repo) : IOrganizationRoleAssignmentService
{
    /// <summary>The 409 reason when the last Owner would be removed or demoted (M6); the front checks
    /// the same rule first and shows its localized message.</summary>
    public const string LastOwnerMessage = "An organization keeps at least one Owner (Organization Admin): promote another member first.";

    public Task<IAsyncDisposable> LockOwnersAsync(CancellationToken ct) => repo.LockOwnersAsync(ct);

    public async Task RefuseLastOwnerLossAsync(
        int userId, IReadOnlyCollection<OrganizationRoleAssignmentDto> requested, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var memberships = await repo.GetMembershipsAsync(userId, ct).ConfigureAwait(false);
        var wanted = requested.Select(item => (item.OrganizationId, Role: item.Role.Trim())).ToList();
        await RefuseLastOwnerLossAsync(memberships, LevelsOf(wanted), ct).ConfigureAwait(false);
    }

    // RBAC audit, finding 10: the user side (roles edited on the user page) gives and takes the same
    // memberships as the organization page, so it keeps the same rule: an Owner level lost is refused
    // when it is the organization's last.
    private async Task RefuseLastOwnerLossAsync(
        List<OrganizationMember> memberships, Dictionary<int, OrganizationRole> levels, CancellationToken ct)
    {
        foreach (var lost in OwnerLevelsLost(memberships, levels))
        {
            if (await repo.CountOwnersAsync(lost, ct).ConfigureAwait(false) <= 1)
                throw new ConflictException(LastOwnerMessage);
        }
    }

    private static List<int> OwnerLevelsLost(List<OrganizationMember> memberships, Dictionary<int, OrganizationRole> levels) =>
        memberships
            .Where(membership => membership.Role == OrganizationRole.Owner
                && (!levels.TryGetValue(membership.OrganizationId, out var level) || level != OrganizationRole.Owner))
            .Select(membership => membership.OrganizationId)
            .ToList();

    private static Dictionary<int, OrganizationRole> LevelsOf(List<(int OrganizationId, string Role)> wanted) =>
        wanted
            .GroupBy(item => item.OrganizationId)
            .ToDictionary(group => group.Key, group => group.Max(item => RoleNames.MembershipFor(item.Role)));

    public async Task GrantMembershipRoleAsync(int userId, int organizationId, OrganizationRole membership, CancellationToken ct)
    {
        var roleName = RoleNames.ForMembership(membership);
        var roleId = await RequireRoleIdAsync(organizationId, roleName, ct).ConfigureAwait(false);
        var held = await repo.GetForUserInOrganizationAsync(userId, organizationId, ct).ConfigureAwait(false);
        if (held.Any(assignment => assignment.RoleId == roleId)) return;
        repo.Add(new UserOrganizationRole { UserId = userId, OrganizationId = organizationId, RoleId = roleId });
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ChangeMembershipRoleAsync(
        int userId, int organizationId, OrganizationRole previous, OrganizationRole current, CancellationToken ct)
    {
        var previousRole = RoleNames.ForMembership(previous);
        var currentRole = RoleNames.ForMembership(current);
        if (string.Equals(previousRole, currentRole, StringComparison.Ordinal)) return;
        var roleIds = await RequireRoleIdsAsync([(organizationId, previousRole), (organizationId, currentRole)], ct).ConfigureAwait(false);
        var previousRoleId = roleIds[(organizationId, previousRole)];
        var currentRoleId = roleIds[(organizationId, currentRole)];
        var held = await repo.GetForUserInOrganizationAsync(userId, organizationId, ct).ConfigureAwait(false);
        foreach (var assignment in held.Where(item => item.RoleId == previousRoleId))
            repo.Remove(assignment);
        if (!held.Any(assignment => assignment.RoleId == currentRoleId))
            repo.Add(new UserOrganizationRole { UserId = userId, OrganizationId = organizationId, RoleId = currentRoleId });
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAllAsync(int userId, int organizationId, CancellationToken ct)
    {
        var held = await repo.GetForUserInOrganizationAsync(userId, organizationId, ct).ConfigureAwait(false);
        if (held.Count == 0) return;
        foreach (var assignment in held)
            repo.Remove(assignment);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> ReplaceForUserAsync(
        int userId, IReadOnlyCollection<OrganizationRoleAssignmentDto> requested, CancellationToken ct)
    {
        var wanted = requested
            .Select(item => (item.OrganizationId, Role: item.Role.Trim()))
            .Distinct()
            .ToList();
        RefuseSiteWideRole(wanted.Select(item => item.Role));
        // R-2: each name is looked up in its organization; another organization's role is "unknown" here.
        var roleIds = await RequireRoleIdsAsync(wanted, ct).ConfigureAwait(false);
        await RequireOrganizationsAsync(wanted.Select(item => item.OrganizationId), ct).ConfigureAwait(false);
        return await ReplaceAsync(
            userId, [.. wanted.Select(item => new WantedRole(item.OrganizationId, item.Role, roleIds[item]))], ct).ConfigureAwait(false);
    }

    /// <summary>A role the user holds, or is to hold, for one organization, already resolved to its id.</summary>
    private readonly record struct WantedRole(int OrganizationId, string Role, int RoleId);

    private async Task<bool> ReplaceAsync(int userId, List<WantedRole> requested, CancellationToken ct)
    {
        List<(int OrganizationId, string Role)> wanted = [.. requested.Select(item => (item.OrganizationId, item.Role))];
        var memberships = await repo.GetMembershipsAsync(userId, ct).ConfigureAwait(false);
        // An Owner level lost is counted under the owners lock, held until saved (finding 10).
        await using var owners = OwnerLevelsLost(memberships, LevelsOf(wanted)).Count > 0
            ? await repo.LockOwnersAsync(ct).ConfigureAwait(false)
            : null;
        await RefuseLastOwnerLossAsync(memberships, LevelsOf(wanted), ct).ConfigureAwait(false);
        var held = await repo.GetForUserAsync(userId, ct).ConfigureAwait(false);
        var wantedKeys = requested.Select(item => (item.OrganizationId, item.RoleId)).ToHashSet();
        var changed = false;

        foreach (var assignment in held.Where(item => !wantedKeys.Contains((item.OrganizationId, item.RoleId))))
        {
            repo.Remove(assignment);
            changed = true;
        }
        var heldKeys = held.Select(item => (item.OrganizationId, item.RoleId)).ToHashSet();
        foreach (var (organizationId, roleId) in wantedKeys.Where(key => !heldKeys.Contains(key)))
        {
            repo.Add(new UserOrganizationRole { UserId = userId, OrganizationId = organizationId, RoleId = roleId });
            changed = true;
        }

        changed |= SyncMemberships(userId, memberships, wanted);
        if (changed)
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        return changed;
    }

    public async Task ValidateAsync(IReadOnlyCollection<OrganizationRoleAssignmentDto> requested, CancellationToken ct)
    {
        var wanted = requested.Select(item => (item.OrganizationId, Role: item.Role.Trim())).ToList();
        RefuseSiteWideRole(wanted.Select(item => item.Role));
        await RequireRoleIdsAsync(wanted, ct).ConfigureAwait(false);
        await RequireOrganizationsAsync(requested.Select(item => item.OrganizationId), ct).ConfigureAwait(false);
    }

    public async Task<bool> AreGivenByOrganizationAdministratorAsync(
        IReadOnlyCollection<OrganizationRoleAssignmentDto> requested, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var own = requested
            .Select(item => (item.OrganizationId, Role: item.Role.Trim()))
            .Where(item => !RoleNames.BuiltInForOneOrganization.Contains(item.Role, StringComparer.Ordinal))
            .ToList();
        if (own.Count == 0) return true;
        // R-2: looked up within each organization, so another organization's role of the same name is not this one.
        var resolved = await repo.ResolveRolesAsync(own, ct).ConfigureAwait(false);
        return own.All(item => resolved.TryGetValue(item, out var role) && role.OrganizationId == item.OrganizationId);
    }

    public async Task<bool> AssignAsync(int userId, int organizationId, int roleId, CancellationToken ct)
    {
        var role = await repo.FindRoleAsync(roleId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException($"Unknown role {roleId}.");
        RefuseSiteWideRole([role.Name]);
        // An organization's own role is held for that organization only (Administration for organization admins, 2026-10-28).
        if (role.OrganizationId is { } owner && owner != organizationId)
            throw new BadRequestException($"The '{role.Name}' role belongs to one organization and is held for it only.");
        await RequireOrganizationsAsync([organizationId], ct).ConfigureAwait(false);
        var held = await repo.GetForUserAsync(userId, ct).ConfigureAwait(false);
        if (held.Any(item => item.OrganizationId == organizationId && item.RoleId == roleId)) return false;

        var requested = held
            .Select(item => new WantedRole(item.OrganizationId, item.Role.Name, item.RoleId))
            .Append(new WantedRole(organizationId, role.Name, role.Id))
            .ToList();
        return await ReplaceAsync(userId, requested, ct).ConfigureAwait(false);
    }

    public async Task<bool> UnassignAsync(int userId, int organizationId, int roleId, CancellationToken ct)
    {
        var held = await repo.GetForUserAsync(userId, ct).ConfigureAwait(false);
        var target = held.FirstOrDefault(item => item.OrganizationId == organizationId && item.RoleId == roleId);
        if (target is null) return false;

        var requested = held
            .Where(item => item != target)
            .Select(item => new WantedRole(item.OrganizationId, item.Role.Name, item.RoleId))
            .ToList();
        await ReplaceAsync(userId, requested, ct).ConfigureAwait(false);
        return true;
    }

    // Membership follows the roles: an organization named makes the user a member at the level of its
    // highest role, an organization no longer named ends the membership (never a member without rights).
    private bool SyncMemberships(
        int userId, List<OrganizationMember> memberships, List<(int OrganizationId, string Role)> wanted)
    {
        var changed = false;
        var levels = LevelsOf(wanted);
        foreach (var membership in memberships)
        {
            if (!levels.TryGetValue(membership.OrganizationId, out var level))
            {
                repo.RemoveMember(membership);
                changed = true;
            }
            else if (membership.Role != level)
            {
                membership.Role = level;
                changed = true;
            }
        }
        var memberOf = memberships.Select(item => item.OrganizationId).ToHashSet();
        foreach (var (organizationId, level) in levels.Where(pair => !memberOf.Contains(pair.Key)))
        {
            repo.AddMember(new OrganizationMember { OrganizationId = organizationId, UserId = userId, Role = level });
            changed = true;
        }
        return changed;
    }

    private static void RefuseSiteWideRole(IEnumerable<string> roleNames)
    {
        if (roleNames.Any(name => string.Equals(name, RoleNames.Admin, StringComparison.OrdinalIgnoreCase)))
            throw new BadRequestException($"The '{RoleNames.Admin}' role is site-wide and is never held for one organization.");
    }

    private async Task<int> RequireRoleIdAsync(int organizationId, string roleName, CancellationToken ct) =>
        (await RequireRoleIdsAsync([(organizationId, roleName)], ct).ConfigureAwait(false))[(organizationId, roleName)];

    /// <summary>
    /// The id of the role each (organization, name) names in that organization (R-2). A name no role of that
    /// organization nor site role carries is refused (400) as unknown: an organization's own role is held for it
    /// only, and the refusal never says that another organization has a role of that name.
    /// </summary>
    private async Task<Dictionary<(int OrganizationId, string Role), int>> RequireRoleIdsAsync(
        IEnumerable<(int OrganizationId, string Role)> wanted, CancellationToken ct)
    {
        var pairs = wanted.Distinct().ToList();
        var found = await repo.ResolveRolesAsync(pairs, ct).ConfigureAwait(false);
        var missing = pairs.Where(pair => !found.ContainsKey(pair)).Select(pair => pair.Role).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new BadRequestException($"Unknown role(s): {string.Join(", ", missing)}.");
        return pairs.ToDictionary(pair => pair, pair => found[pair].Id);
    }

    private async Task RequireOrganizationsAsync(IEnumerable<int> organizationIds, CancellationToken ct)
    {
        var ids = organizationIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var existing = await repo.GetExistingOrganizationIdsAsync(ids, ct).ConfigureAwait(false);
        var missing = ids.Except(existing).ToList();
        if (missing.Count > 0)
            throw new BadRequestException($"Unknown organization(s): {string.Join(", ", missing)}.");
    }
}
