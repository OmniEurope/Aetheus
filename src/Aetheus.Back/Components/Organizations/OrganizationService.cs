// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Organizations;
namespace Aetheus.Back.Components.Organizations;

public class OrganizationService(IOrganizationRepository repo, IResourceAuthorizationService authz, TimeProvider timeProvider, IAdminChangeNotifier notifier, IUserChangeNotifier userNotifier, IAuditService audit, IOrganizationRoleAssignmentService roleAssignments) : IOrganizationService
{
    public Task<PaginatedResult<OrganizationDto>> GetOrganizationsAsync(string? search, PaginationRequest request, CancellationToken ct) =>
        GetOrganizationsAsync(search, request, null, ct);

    public async Task<List<AdministeredOrganizationDto>> GetOrganizationNamesAsync(IReadOnlyList<int>? organizationIds, CancellationToken ct) =>
        [.. (await repo.GetNamesAsync(organizationIds, ct).ConfigureAwait(false))
            .Select(row => new AdministeredOrganizationDto(row.Id, row.Name, []))];

    public async Task<PaginatedResult<OrganizationDto>> GetOrganizationsAsync(
        string? search, PaginationRequest request, IReadOnlyList<int>? organizationIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = PaginationDefaults.Clamp(request.PageSize);
        var (items, total) = await repo.GetPagedAsync(
            search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters, organizationIds).ConfigureAwait(false);
        return new PaginatedResult<OrganizationDto>
        {
            Items = items.Select(MapToListDto).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<OrganizationDetailDto?> GetOrganizationAsync(int id, CancellationToken ct)
    {
        var org = await repo.GetWithMembersAndProjectsAsync(id, ct).ConfigureAwait(false);
        if (org is null) return null;
        return new OrganizationDetailDto(
            org.Id,
            org.Name,
            org.Slug,
            org.Description,
            org.CreatedAt,
            org.UpdatedAt,
            org.Members.Select(m => new OrganizationMemberDto(
                m.Id, m.UserId, m.User.Username, m.User.Email, m.Role, m.CreatedAt)).ToList(),
            org.Projects.Select(p => new OrganizationProjectDto(p.Id, p.Name, p.Status)).ToList(),
            org.AutoCreateDevelopBranch);
    }

    public async Task<OrganizationDto> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken ct)
    {
        if (await repo.NameExistsAsync(request.Name, null, ct).ConfigureAwait(false))
            throw new ConflictException($"Organization name '{request.Name}' already exists.");
        // The slug is optional (R-22): an empty one is generated from the name, made unique.
        var slug = string.IsNullOrWhiteSpace(request.Slug)
            ? await GenerateUniqueSlugAsync(request.Name, null, ct).ConfigureAwait(false)
            : request.Slug.Trim().ToLowerInvariant();
        if (await repo.SlugExistsAsync(slug, null, ct).ConfigureAwait(false))
            throw new ConflictException($"Organization slug '{slug}' already exists.");

        var org = new Organization
        {
            Name = request.Name.Trim(),
            Slug = slug,
            Description = request.Description?.Trim() ?? string.Empty,
            AutoCreateDevelopBranch = request.AutoCreateDevelopBranch
        };
        await repo.AddAsync(org, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Organization.Created", "Organization", org.Id,
            $"Organization '{org.Name}' created", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Organization, org.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);
        return MapToListDto(org);
    }

    private async Task<string> GenerateUniqueSlugAsync(string name, int? excludeId, CancellationToken ct)
    {
        var baseSlug = OrganizationSlug.FromName(name);
        var candidate = baseSlug;
        for (var suffix = 2; await repo.SlugExistsAsync(candidate, excludeId, ct).ConfigureAwait(false); suffix++)
            candidate = $"{baseSlug}-{suffix}";
        return candidate;
    }

    public async Task<OrganizationDto?> UpdateOrganizationAsync(int id, UpdateOrganizationRequest request, CancellationToken ct)
    {
        var org = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (org is null) return null;

        if (await repo.NameExistsAsync(request.Name, id, ct).ConfigureAwait(false))
            throw new ConflictException($"Organization name '{request.Name}' already exists.");
        // An empty slug keeps the current one (generated from the name if the row somehow has none).
        var slug = !string.IsNullOrWhiteSpace(request.Slug)
            ? request.Slug.Trim().ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(org.Slug)
                ? org.Slug
                : await GenerateUniqueSlugAsync(request.Name, id, ct).ConfigureAwait(false);
        if (await repo.SlugExistsAsync(slug, id, ct).ConfigureAwait(false))
            throw new ConflictException($"Organization slug '{slug}' already exists.");

        org.Name = request.Name.Trim();
        org.Slug = slug;
        org.Description = request.Description?.Trim() ?? string.Empty;
        org.AutoCreateDevelopBranch = request.AutoCreateDevelopBranch;
        org.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Organization.Updated", "Organization", org.Id,
            $"Organization '{org.Name}' updated", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Organization, org.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return MapToListDto(org);
    }

    public async Task<bool> DeleteOrganizationAsync(int id, CancellationToken ct)
    {
        // Capture members before the delete so we can invalidate their org-scoped authz cache and
        // push a refresh - deleting the org removes their org-scoped resource access.
        var members = await repo.GetMembersForNotificationAsync(id, ct).ConfigureAwait(false);
        var deleted = await repo.DeleteAsync(id, ct).ConfigureAwait(false);
        if (deleted)
        {
            await audit.LogAsync("Organization.Deleted", "Organization", id,
                $"Organization {id} deleted", ct).ConfigureAwait(false);
            foreach (var m in members)
                authz.InvalidateRoleCache(m.Username);
            await notifier.BroadcastAsync(AdminEntities.Organization, id, EntityChangeOps.Deleted, ct).ConfigureAwait(false);
            foreach (var m in members)
                await userNotifier.NotifyPermissionsChangedAsync(m.UserId, PermissionChangeReasons.OrganizationMembership, ct).ConfigureAwait(false);
        }
        return deleted;
    }

    public async Task<OrganizationMemberDto> AddMemberAsync(int organizationId, AddOrganizationMemberRequest request, CancellationToken ct)
    {
        var org = await repo.GetByIdAsync(organizationId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Organization {organizationId} not found.");

        var user = await FindUserToAddAsync(request, ct).ConfigureAwait(false);

        var existing = await repo.GetMemberAsync(organizationId, user.Id, ct).ConfigureAwait(false);
        if (existing is not null)
            throw new ConflictException($"User '{user.Username}' is already a member of '{org.Name}'.");

        var member = new OrganizationMember
        {
            OrganizationId = organizationId,
            UserId = user.Id,
            Role = request.Role
        };
        await repo.AddMemberAsync(member, ct).ConfigureAwait(false);
        // R-45: a member is never without rights; the membership level gives its role. Both share the
        // request's unit of work, so the membership and its role are saved together.
        await roleAssignments.GrantMembershipRoleAsync(user.Id, organizationId, member.Role, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Organization.MemberAdded", "Organization", organizationId,
            $"User '{user.Username}' added as {member.Role}", ct).ConfigureAwait(false);
        authz.InvalidateRoleCache(user.Username);
        await notifier.BroadcastAsync(AdminEntities.Organization, organizationId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        await userNotifier.NotifyPermissionsChangedAsync(user.Id, PermissionChangeReasons.OrganizationMembership, ct).ConfigureAwait(false);
        return new OrganizationMemberDto(member.Id, user.Id, user.Username, user.Email, member.Role, member.CreatedAt);
    }

    public async Task<OrganizationMemberDto?> UpdateMemberAsync(int organizationId, int memberId, UpdateOrganizationMemberRequest request, CancellationToken ct)
    {
        var member = await repo.GetMemberWithUserByIdAsync(memberId, organizationId, ct).ConfigureAwait(false);
        if (member is null) return null;
        var demotesOwner = member.Role == OrganizationRole.Owner && request.Role != OrganizationRole.Owner;
        // RBAC audit, finding 10: counted and saved under the owners lock, so two Owners demoted at once
        // never both pass the count.
        await using var owners = demotesOwner ? await roleAssignments.LockOwnersAsync(ct).ConfigureAwait(false) : null;
        if (demotesOwner)
            await RefuseLastOwnerAsync(organizationId, ct).ConfigureAwait(false);
        var previousRole = member.Role;
        member.Role = request.Role;
        // The new level's role replaces the old one's in the same unit of work as the level itself.
        await roleAssignments.ChangeMembershipRoleAsync(
            member.UserId, organizationId, previousRole, member.Role, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Organization.MemberRoleUpdated", "Organization", organizationId,
            $"User '{member.User.Username}' role changed to {member.Role}", ct).ConfigureAwait(false);
        authz.InvalidateRoleCache(member.User.Username);
        await notifier.BroadcastAsync(AdminEntities.Organization, organizationId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        await userNotifier.NotifyPermissionsChangedAsync(member.UserId, PermissionChangeReasons.OrganizationMembership, ct).ConfigureAwait(false);
        return new OrganizationMemberDto(member.Id, member.UserId, member.User.Username, member.User.Email, member.Role, member.CreatedAt);
    }

    public async Task<bool> RemoveMemberAsync(int organizationId, int memberId, CancellationToken ct)
    {
        var member = await repo.GetMemberWithUserByIdAsync(memberId, organizationId, ct).ConfigureAwait(false);
        var removesOwner = member?.Role == OrganizationRole.Owner;
        await using var owners = removesOwner ? await roleAssignments.LockOwnersAsync(ct).ConfigureAwait(false) : null;
        if (removesOwner)
            await RefuseLastOwnerAsync(organizationId, ct).ConfigureAwait(false);
        // A former member keeps no role for the organization (ADR-059).
        if (member is not null)
            await roleAssignments.RemoveAllAsync(member.UserId, organizationId, ct).ConfigureAwait(false);
        var removed = await repo.RemoveMemberAsync(organizationId, memberId, ct).ConfigureAwait(false);
        if (removed && member?.User is not null)
            authz.InvalidateRoleCache(member.User.Username);
        if (removed)
        {
            await audit.LogAsync("Organization.MemberRemoved", "Organization", organizationId,
                $"User '{member?.User.Username ?? memberId.ToString()}' removed", ct).ConfigureAwait(false);
            await notifier.BroadcastAsync(AdminEntities.Organization, organizationId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
            if (member is not null)
                await userNotifier.NotifyPermissionsChangedAsync(member.UserId, PermissionChangeReasons.OrganizationMembership, ct).ConfigureAwait(false);
        }
        return removed;
    }

    /// <summary>The 409 reason when the last Owner would be removed or demoted (M6); the front checks
    /// the same rule first and shows its localized message.</summary>
    public const string LastOwnerMessage = OrganizationRoleAssignmentService.LastOwnerMessage;

    private async Task<User> FindUserToAddAsync(AddOrganizationMemberRequest request, CancellationToken ct)
    {
        if (request.UserId is { } userId)
            return await repo.GetUserByIdAsync(userId, ct).ConfigureAwait(false)
                ?? throw new NotFoundException($"User {userId} not found.");
        var login = request.UserLogin?.Trim();
        if (string.IsNullOrEmpty(login))
            throw new BadRequestException("Name the user to add by username or e-mail address.");
        return await repo.GetUserByLoginAsync(login, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("No user has this exact username or e-mail address.");
    }

    // M6: the last Owner (who holds the Organization Admin role) is never removed nor demoted.
    private async Task RefuseLastOwnerAsync(int organizationId, CancellationToken ct)
    {
        if (await repo.CountOwnersAsync(organizationId, ct).ConfigureAwait(false) <= 1)
            throw new ConflictException(LastOwnerMessage);
    }

    public async Task AssignProjectsAsync(int organizationId, List<int> projectIds, CancellationToken ct)
    {
        _ = await repo.GetByIdAsync(organizationId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Organization {organizationId} not found.");

        var distinct = projectIds.Distinct().ToList();
        var existingCount = await repo.CountExistingProjectsAsync(distinct, ct).ConfigureAwait(false);
        if (existingCount != distinct.Count)
            throw new BadRequestException("One or more project IDs do not exist.");

        // The organizations that lose the projects, read before the move: their members keep a
        // cached "member of the owner" verdict for five minutes unless it is evicted too.
        var sourceOrganizationIds = await repo.GetProjectOrganizationIdsAsync(distinct, ct).ConfigureAwait(false);
        await repo.AssignProjectsAsync(organizationId, distinct, ct).ConfigureAwait(false);
        await audit.LogAsync("Organization.ProjectsAssigned", "Organization", organizationId,
            $"Assigned project IDs: {string.Join(", ", distinct)}", ct).ConfigureAwait(false);

        // Reassigning projects changes what the members of the source and target organizations can
        // reach through org-scoped access, so invalidate their authz cache and push a refresh.
        var members = new List<(int UserId, string Username)>();
        foreach (var affectedOrganizationId in sourceOrganizationIds.Append(organizationId).Distinct())
            members.AddRange(await repo.GetMembersForNotificationAsync(affectedOrganizationId, ct).ConfigureAwait(false));
        members = members.DistinctBy(m => m.UserId).ToList();
        foreach (var m in members)
            authz.InvalidateRoleCache(m.Username);
        await notifier.BroadcastAsync(AdminEntities.Organization, organizationId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        foreach (var m in members)
            await userNotifier.NotifyPermissionsChangedAsync(m.UserId, PermissionChangeReasons.OrganizationMembership, ct).ConfigureAwait(false);
    }

    public async Task<List<MyOrganizationDto>> GetMyOrganizationsAsync(string username, CancellationToken ct)
    {
        var rows = await repo.GetOrganizationsForUsernameAsync(username, ct).ConfigureAwait(false);
        return rows.Select(r => new MyOrganizationDto(r.Organization.Id, r.Organization.Name, r.Organization.Slug, r.Role)).ToList();
    }

    public async Task<List<UserOrganizationDto>> GetOrganizationsForUserAsync(int userId, CancellationToken ct)
    {
        var rows = await repo.GetOrganizationsForUserIdAsync(userId, ct).ConfigureAwait(false);
        return rows.Select(r => new UserOrganizationDto(
            r.Organization.Id, r.MemberId, r.Organization.Name, r.Organization.Slug, r.Role)).ToList();
    }

    public Task<int?> GetDefaultOrganizationIdAsync(CancellationToken ct)
        => repo.GetDefaultOrganizationIdAsync(ct);

    private static OrganizationDto MapToListDto(Organization o) => new(
        o.Id, o.Name, o.Slug, o.Description,
        o.Members?.Count ?? 0, o.Projects?.Count ?? 0,
        o.CreatedAt, o.UpdatedAt);
}
