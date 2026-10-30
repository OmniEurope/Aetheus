// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Users;

public class UserService(IUserRepository repo, IHttpContextAccessor httpContextAccessor, IAuditService audit, IResourceAuthorizationService authz, IMemoryCache cache, TimeProvider timeProvider, IAdminChangeNotifier notifier, IUserChangeNotifier userNotifier, IDomainEventDispatcher domainEvents, IOrganizationRoleAssignmentService roleAssignments) : IUserService
{
    private readonly UserPipelineOwnershipGuard _pipelineOwnership = new(repo, audit, domainEvents);

    public async Task<PaginatedResult<UserDto>> GetUsersAsync(PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetUsersPagedProjectedAsync(
            request.Search, page, pageSize, ct, request.SortBy, request.SortDescending, request.Filters)
            .ConfigureAwait(false);

        return new PaginatedResult<UserDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PaginatedResult<UserDto>> GetOrganizationUsersAsync(
        PaginationRequest request, IReadOnlyList<int> organizationIds, bool siteAdmin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(organizationIds);
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetUsersPagedProjectedAsync(
            request.Search, page, pageSize, ct, request.SortBy, request.SortDescending, request.Filters, organizationIds)
            .ConfigureAwait(false);

        return new PaginatedResult<UserDto>
        {
            Items = siteAdmin ? items : [.. items.Select(user => user with { Email = null, Roles = [] })],
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<UserDto?> GetUserDetailAsync(int id, CancellationToken ct = default)
    {
        var user = await repo.GetUserDetailAsync(id, ct).ConfigureAwait(false);
        if (user is null) return null;

        return new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            IsActive = user.IsActive,
            MustChangePassword = user.MustChangePassword,
            Roles = SiteRoles(user),
            OrganizationRoles = OrganizationRoles(user),
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt,
            LastActivityAt = user.LastActivityAt
        };
    }

    public async Task<UserDto?> GetCurrentUserAsync(CancellationToken ct = default)
    {
        var username = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrEmpty(username)) return null;
        var user = await repo.FindByUsernameAsync(username, ct).ConfigureAwait(false);
        return user is null ? null : MapToDto(user);
    }

    public async Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        // F-24: normalize usernames to avoid case-only / whitespace duplicates.
        var normalized = (request.Username ?? string.Empty).Trim().ToLowerInvariant();
        var existing = await repo.FindByUsernameAsync(normalized, ct).ConfigureAwait(false);
        if (existing is not null)
            throw new ConflictException($"Username '{normalized}' is already taken.");
        var email = await RequireAvailableEmailAsync(request.Email, null, ct).ConfigureAwait(false);
        RefuseOrganizationRolesHeldSiteWide(request.Roles);
        await roleAssignments.ValidateAsync(request.OrganizationRoles, ct).ConfigureAwait(false);

        var user = new User
        {
            Username = normalized,
            // R-14 (owner decision 2026-10-28): no password at creation. The hash is of 32 random bytes nobody
            // ever sees, so no password signs in until the person chooses theirs from the welcome mail's link.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            Email = email,
            IsActive = true,
            MustChangePassword = false
        };

        await repo.AddUserAsync(user, ct).ConfigureAwait(false);

        if (request.Roles.Count > 0)
        {
            var roles = await repo.GetRolesByNamesAsync(request.Roles, ct).ConfigureAwait(false);
            foreach (var role in roles)
                user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, Role = role });

            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        // R-47: each organization role makes the new user a member of that organization.
        if (request.OrganizationRoles.Count > 0)
            await roleAssignments.ReplaceForUserAsync(user.Id, request.OrganizationRoles, ct).ConfigureAwait(false);

        var detail = await repo.GetUserDetailAsync(user.Id, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "User", user.Id, user.Username, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.User, user.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);
        // R-14: the account exists whatever becomes of its welcome mail; the answer says which.
        var welcome = new UserWelcomeRequestedEvent(user.Id, user.Username, email);
        await domainEvents.DispatchAsync(welcome, ct).ConfigureAwait(false);
        if (SiteRoles(detail!).Contains(RoleNames.Admin))
            await AnnounceSiteAdminAsync(detail!, ct).ConfigureAwait(false);
        return MapToDto(detail!) with
        {
            WelcomeMail = welcome.Outcome ?? new WelcomeMailResultDto
            {
                Status = WelcomeMailStatus.Failed,
                To = email,
                Message = "The welcome mail was not sent: it could not be written (see the server log)."
            }
        };
    }

    public async Task<UserDto?> UpdateUserAsync(int id, UpdateUserRequest request, CancellationToken ct = default)
    {
        var user = await repo.GetUserDetailAsync(id, ct).ConfigureAwait(false);
        if (user is null) return null;
        var normalized = (request.Username ?? string.Empty).Trim().ToLowerInvariant();
        await EnsureUsernameAvailableAsync(id, user.Username, normalized, ct).ConfigureAwait(false);
        var email = await RequireAvailableEmailAsync(request.Email, id, ct).ConfigureAwait(false);
        // Every refusal is decided before the entity changes, because the pipeline transfer below
        // saves its own changes and must not carry a half-applied update of this user with it.
        var deactivating = user.IsActive && !request.IsActive;
        var requestedRolesList = await ValidateRequestedRolesAsync(user, request, ct).ConfigureAwait(false);
        var roleChange = DescribeRoleChange(user, requestedRolesList);
        // AUD-05: a change that can remove an active administrator holds the administrator-set lock
        // until its commit, so two such changes never both pass the "last administrator" count.
        await using var administratorSet =
            await LockAdministratorSetWhenLosingOneAsync(roleChange, deactivating, ct).ConfigureAwait(false);
        await EnsureSecurityChangeAllowedAsync(user, roleChange, deactivating, ct).ConfigureAwait(false);
        var previousUsername = user.Username;
        var renamed = !string.Equals(previousUsername, normalized, StringComparison.Ordinal);
        var forcedChangeArmed = !user.MustChangePassword && request.MustChangePassword;
        if (deactivating)
            await _pipelineOwnership.EnsureOwnedPipelinesHandledAsync(
                user.Id, user.Username, request.TransferPipelinesToUsername, request.ForceWithOwnedPipelines,
                "Deactivated", ct).ConfigureAwait(false);
        await ApplyUpdateAsync(
            user, request, normalized, email, requestedRolesList, roleChange,
            endsSessions: deactivating || renamed || forcedChangeArmed, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        var organizationRolesChanged = await ReplaceOrganizationRolesAsync(user.Id, request, ct).ConfigureAwait(false);
        InvalidateIdentityCaches(user, previousUsername, renamed, forcedChangeArmed);
        return await PublishUpdatedUserAsync(user, organizationRolesChanged, roleChange, deactivating, ct).ConfigureAwait(false);
    }

    // The administrator-set lock is held only by a change that can remove an active administrator.
    private async Task<IAsyncDisposable?> LockAdministratorSetWhenLosingOneAsync(
        RoleChange roleChange, bool deactivating, CancellationToken ct) =>
        roleChange.WasAdmin && (roleChange.LosingAdmin || deactivating)
            ? await repo.LockAdministratorSetAsync(ct).ConfigureAwait(false)
            : null;

    // Everything that follows the saved update: the notifications, the announcement of a new site
    // administrator and the end of a deactivated user's sessions.
    private async Task<UserDto> PublishUpdatedUserAsync(
        User user, bool organizationRolesChanged, RoleChange roleChange, bool deactivating, CancellationToken ct)
    {
        await PublishUserUpdateAsync(user, roleChange.HasChanged || organizationRolesChanged, deactivating, ct).ConfigureAwait(false);
        if (roleChange.WillBeAdmin && !roleChange.WasAdmin)
            await AnnounceSiteAdminAsync(user, ct).ConfigureAwait(false);
        if (organizationRolesChanged)
            user = await repo.GetUserDetailAsync(user.Id, ct).ConfigureAwait(false) ?? user;
        if (deactivating)
            await EndSessionAsync(user.Id, UserSessionEndReasons.Deactivated, ct).ConfigureAwait(false);
        return MapToDto(user);
    }

    private async Task<bool> ReplaceOrganizationRolesAsync(int userId, UpdateUserRequest request, CancellationToken ct) =>
        request.OrganizationRoles is { } requestedOrganizationRoles
        && await roleAssignments.ReplaceForUserAsync(userId, requestedOrganizationRoles, ct).ConfigureAwait(false);

    // Every refusal about the requested roles, before anything is saved; returns the site-wide role names.
    private async Task<List<string>> ValidateRequestedRolesAsync(User user, UpdateUserRequest request, CancellationToken ct)
    {
        var requestedRolesList = request.Roles ?? [];
        RefuseOrganizationRolesHeldSiteWide(requestedRolesList);
        if (request.OrganizationRoles is { } organizationRoles)
        {
            await roleAssignments.ValidateAsync(organizationRoles, ct).ConfigureAwait(false);
            // RBAC audit, finding 10: refused here, before anything is saved, rather than half-way.
            await roleAssignments.RefuseLastOwnerLossAsync(user.Id, organizationRoles, ct).ConfigureAwait(false);
        }
        return requestedRolesList;
    }

    private async Task ApplyUpdateAsync(
        User user, UpdateUserRequest request, string normalizedUsername, string email, List<string> requestedRoles,
        RoleChange roleChange, bool endsSessions, CancellationToken ct)
    {
        user.Username = normalizedUsername;
        user.Email = email;
        user.IsActive = request.IsActive;
        user.MustChangePassword = request.MustChangePassword;
        user.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        if (roleChange.HasChanged)
            await ApplyRolesAsync(user, requestedRoles, ct).ConfigureAwait(false);
        // AUD-02 and AUD-03: a rename or a newly required password change also ends the sessions in
        // flight, as a role change or a deactivation does. The old tokens carry the old name, or lack the
        // forced-change claim the middleware enforces; the rotated stamp refuses them and a refresh mints
        // tokens from the current row.
        if (roleChange.HasChanged || endsSessions) RotateSecurityStamp(user);
    }

    // A rename drops the cached stamp and both names' role caches; a newly forced password change drops
    // the cached stamp so the rotated one is read at once.
    private void InvalidateIdentityCaches(User user, string previousUsername, bool renamed, bool forcedChangeArmed)
    {
        if (!renamed && !forcedChangeArmed) return;
        cache.Remove($"sec-stamp:{user.Id}");
        if (!renamed) return;
        authz.InvalidateRoleCache(previousUsername);
        authz.InvalidateRoleCache(user.Username);
    }

    private async Task EnsureUsernameAvailableAsync(
        int userId,
        string currentUsername,
        string normalizedUsername,
        CancellationToken ct)
    {
        if (string.Equals(currentUsername, normalizedUsername, StringComparison.Ordinal)) return;
        var existing = await repo.FindByUsernameAsync(normalizedUsername, ct).ConfigureAwait(false);
        if (existing is not null && existing.Id != userId)
            throw new ConflictException($"Username '{normalizedUsername}' is already taken.");
    }

    // PLAN-003 P3-2: the e-mail is the sign-in identifier, so every account has one, stored normalized
    // and held by no other account. The refusal names a code the front says in the user's language.
    private async Task<string> RequireAvailableEmailAsync(string? requested, int? userId, CancellationToken ct)
    {
        var email = UserEmail.Normalize(requested)
            ?? throw new BadRequestException("An e-mail address is required: it is the sign-in identifier.");
        if (await repo.EmailInUseAsync(email, userId, ct).ConfigureAwait(false))
            throw new ConflictException("This e-mail address is already used by another account.", UserErrorCodes.EmailInUse);
        return email;
    }

    private static RoleChange DescribeRoleChange(User user, IReadOnlyCollection<string> requestedRoles)
    {
        // Only Admin is held site-wide (ADR-059); the inert legacy rows of other roles are no change.
        var previous = user.UserRoles.Select(item => item.Role.Name)
            .Where(name => name == RoleNames.Admin).OrderBy(name => name).ToList();
        var requested = requestedRoles.OrderBy(name => name).ToList();
        return new RoleChange(
            !previous.SequenceEqual(requested, StringComparer.Ordinal),
            previous.Contains("Admin"),
            requested.Contains("Admin"));
    }

    private async Task EnsureSecurityChangeAllowedAsync(
        User user,
        RoleChange roleChange,
        bool deactivating,
        CancellationToken ct)
    {
        if (!roleChange.LosingAdmin && !deactivating) return;
        var callerName = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name);
        var changingOwnAccount = callerName is not null
            && string.Equals(callerName, user.Username, StringComparison.Ordinal);
        if (changingOwnAccount && roleChange.LosingAdmin)
            throw new BadRequestException("You cannot remove the Admin role from your own account.");
        if (changingOwnAccount && deactivating)
            throw new BadRequestException("You cannot deactivate your own account.");
        if (!roleChange.WasAdmin) return;
        var admins = await repo.CountActiveAdminsAsync(ct).ConfigureAwait(false);
        if (admins <= 1)
            throw new BadRequestException("Cannot remove the last active administrator.");
    }

    private async Task ApplyRolesAsync(
        User user,
        List<string> requestedRoles,
        CancellationToken ct)
    {
        var roles = await repo.GetRolesByNamesAsync(requestedRoles, ct).ConfigureAwait(false);
        var requestedRoleIds = roles.Select(role => role.Id).ToHashSet();
        // Only the Admin row is replaced: the inert legacy rows of other roles stay, so rolling the
        // ScopeRoleAssignmentsToOrganizations migration back still finds them (ADR-059).
        user.UserRoles.RemoveAll(userRole => userRole.Role.Name == RoleNames.Admin && !requestedRoleIds.Contains(userRole.RoleId));
        var assignedRoleIds = user.UserRoles.Select(userRole => userRole.RoleId).ToHashSet();
        foreach (var role in roles.Where(role => !assignedRoleIds.Contains(role.Id)))
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, Role = role });
    }

    private async Task PublishUserUpdateAsync(
        User user,
        bool rolesChanged,
        bool deactivating,
        CancellationToken ct)
    {
        if (rolesChanged || deactivating) InvalidateAuthzCaches(user);
        await audit.LogAsync("Updated", "User", user.Id, user.Username, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.User, user.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        if (rolesChanged)
            await notifier.BroadcastAsync(AdminEntities.Role, 0, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        if (rolesChanged || deactivating)
            await userNotifier.NotifyPermissionsChangedAsync(
                user.Id, PermissionChangeReasons.Roles, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteUserAsync(
        int id, CancellationToken ct = default, string? transferPipelinesTo = null, bool forceWithOwnedPipelines = false)
    {
        var user = await repo.FindUserAsync(id, ct).ConfigureAwait(false);
        if (user is null) return false;

        // F-13: prevent self-deletion.
        var callerName = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name);
        if (callerName is not null && string.Equals(callerName, user.Username, StringComparison.Ordinal))
            throw new BadRequestException("You cannot delete your own account.");

        // F-13: prevent removal of the last active admin.
        var userDetail = await repo.GetUserDetailAsync(id, ct).ConfigureAwait(false);
        var removesAdministrator = userDetail is not null && userDetail.IsActive
            && userDetail.UserRoles.Any(ur => ur.Role.Name == "Admin");
        await using var administratorSet = removesAdministrator
            ? await repo.LockAdministratorSetAsync(ct).ConfigureAwait(false)
            : null;
        if (removesAdministrator)
        {
            var admins = await repo.CountActiveAdminsAsync(ct).ConfigureAwait(false);
            if (admins <= 1)
                throw new BadRequestException("Cannot delete the last active administrator.");
        }

        await _pipelineOwnership.EnsureOwnedPipelinesHandledAsync(
            user.Id, user.Username, transferPipelinesTo, forceWithOwnedPipelines, "Deleted", ct).ConfigureAwait(false);

        var name = user.Username;
        await repo.RemoveUserAsync(user, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "User", id, name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.User, id, EntityChangeOps.Deleted, ct).ConfigureAwait(false);
        await EndSessionAsync(id, UserSessionEndReasons.Deleted, ct).ConfigureAwait(false);
        return true;
    }

    // Tells the modules holding per-user state in memory (the typed mailbox passwords) that this
    // user's session is over. In-process and awaited, so it is done when the request returns.
    private Task EndSessionAsync(int userId, string reason, CancellationToken ct) =>
        domainEvents.DispatchAsync(new UserSessionEndedEvent(userId, reason), ct);

    public async Task<bool> ChangePasswordAsync(int id, ChangeUserPasswordRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await repo.FindUserAsync(id, ct).ConfigureAwait(false);
        if (user is null) return false;

        var (allowed, isSelf, isAdmin) = AuthorizePasswordChange(user, request);
        if (!allowed) return false;

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        // A self-service password change clears the forced-change requirement. An administrator
        // resetting another user's password sets it so the user must choose their own password.
        if (isSelf)
            user.MustChangePassword = false;
        else if (isAdmin)
            user.MustChangePassword = true;
        // A reset by someone else also ends the personal access tokens: the security-stamp rotation above
        // revokes the JWTs, but a PAT is checked against its own row, not the stamp (SITE-SEC-001).
        var revokedTokens = isSelf
            ? 0
            : await repo.RevokePersonalAccessTokensAsync(user.Id, user.UpdatedAt, ct).ConfigureAwait(false);
        // AUD-01: every refresh token ends with the old password, the holder's own included; otherwise
        // a refresh token taken before the change mints a valid access token with the new stamp.
        await repo.RevokeRefreshTokensAsync(user.Id, user.UpdatedAt, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await AfterPasswordChangeAsync(user, isSelf, revokedTokens, ct).ConfigureAwait(false);
        return true;
    }

    // Determine if the caller is acting on themselves or on another user.
    // No HttpContext (e.g. background worker, integration tests) is treated as a system reset
    // and bypasses the self-service current-password check.
    // Self-service changes always require the current password.
    // Admin / system-driven resets are allowed without it.
    private (bool Allowed, bool IsSelf, bool IsAdmin) AuthorizePasswordChange(User user, ChangeUserPasswordRequest request)
    {
        var caller = httpContextAccessor.HttpContext?.User;
        var callerName = caller?.FindFirstValue(ClaimTypes.Name);
        var isSelf = callerName is not null && string.Equals(callerName, user.Username, StringComparison.Ordinal);
        var isAdmin = caller?.IsInRole("Admin") == true;
        var isSystem = caller is null;

        var allowed = isSelf
            ? !string.IsNullOrEmpty(request.CurrentPassword)
                && BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash)
            : isAdmin || isSystem;
        return (allowed, isSelf, isAdmin);
    }

    private async Task AfterPasswordChangeAsync(User user, bool isSelf, int revokedTokens, CancellationToken ct)
    {
        // Evict AFTER the commit (challenge TOCTOU, same class as UpdateUser/Assign/Unassign): evicting
        // before the write lets a concurrent read re-cache the pre-rotation security stamp / role set for
        // the TTL, defeating the revocation the security-stamp rotation is meant to force.
        cache.Remove($"sec-stamp:{user.Id}");
        authz.InvalidateRoleCache(user.Username);
        await audit.LogAsync(
            isSelf ? "PasswordChanged" : "PasswordReset",
            "User", user.Id,
            revokedTokens > 0 ? $"{user.Username}; {revokedTokens} personal access token(s) revoked" : user.Username,
            ct).ConfigureAwait(false);
        // The client signs out right after a self-service change, with a token the rotated stamp already
        // refuses: its logout call cannot reach the server, so the session end is announced here.
        if (isSelf)
            await EndSessionAsync(user.Id, UserSessionEndReasons.PasswordChanged, ct).ConfigureAwait(false);
    }

    public async Task<bool> ChangeOwnPasswordAsync(ChangeUserPasswordRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolve the caller's own id from the authenticated principal so the client never
        // supplies (or spoofs) a target id. Delegates to ChangePasswordAsync, whose isSelf
        // branch enforces the current-password verification for self-service changes.
        var username = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrEmpty(username)) return false;

        var user = await repo.FindByUsernameAsync(username, ct).ConfigureAwait(false);
        if (user is null) return false;

        return await ChangePasswordAsync(user.Id, request, ct).ConfigureAwait(false);
    }

    public async Task<List<string>> GetRolesAsync(CancellationToken ct = default)
    {
        return await repo.GetAllRoleNamesAsync(ct).ConfigureAwait(false);
    }

    public async Task AssignRoleAsync(int userId, int roleId, int? organizationId, CancellationToken ct = default)
    {
        var user = await repo.GetUserDetailAsync(userId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"User {userId} not found.");
        var role = await repo.FindRoleAsync(roleId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Role {roleId} not found.");

        if (!IsSiteRole(role.Name))
        {
            // ADR-059: every role but Admin is held for one organization, and makes the user a member.
            if (organizationId is not { } organization)
                throw new BadRequestException($"The '{role.Name}' role is held for one organization; choose the organization.");
            if (!await roleAssignments.AssignAsync(user.Id, organization, role.Id, ct).ConfigureAwait(false))
                throw new ConflictException($"User '{user.Username}' already holds role '{role.Name}' for this organization.");
            await PersistRoleChangeAsync(user, ct).ConfigureAwait(false);
            return;
        }
        if (organizationId is not null)
            throw new BadRequestException($"The '{role.Name}' role is site-wide and is never held for one organization.");

        if (user.UserRoles.Any(ur => ur.RoleId == roleId))
            throw new ConflictException($"User '{user.Username}' already holds role '{role.Name}'.");

        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await PersistRoleChangeAsync(user, ct).ConfigureAwait(false);
        await AnnounceSiteAdminAsync(user, ct).ConfigureAwait(false);
    }

    public async Task<bool> UnassignRoleAsync(int userId, int roleId, int? organizationId, CancellationToken ct = default)
    {
        var user = await repo.GetUserDetailAsync(userId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"User {userId} not found.");

        if (organizationId is { } organization)
        {
            var held = user.OrganizationRoles.FirstOrDefault(item => item.RoleId == roleId && item.OrganizationId == organization);
            if (held is null
                || !await roleAssignments.UnassignAsync(user.Id, organization, roleId, ct).ConfigureAwait(false))
                return false;
            await PersistRoleChangeAsync(user, ct).ConfigureAwait(false);
            return true;
        }

        if (user.OrganizationRoles.Any(item => item.RoleId == roleId))
        {
            // No organization named (the role's member list shows each holder once): the role leaves
            // every organization it is held for, and an organization left without role ends the membership.
            var remaining = user.OrganizationRoles
                .Where(item => item.RoleId != roleId)
                .Select(item => new OrganizationRoleAssignmentDto { OrganizationId = item.OrganizationId, Role = item.Role.Name })
                .ToList();
            await roleAssignments.ReplaceForUserAsync(user.Id, remaining, ct).ConfigureAwait(false);
            var legacy = user.UserRoles.FirstOrDefault(ur => ur.RoleId == roleId);
            if (legacy is not null) user.UserRoles.Remove(legacy);
            await PersistRoleChangeAsync(user, ct).ConfigureAwait(false);
            return true;
        }

        var target = user.UserRoles.FirstOrDefault(ur => ur.RoleId == roleId);
        if (target is null) return false;

        // Mirror DeleteUserAsync's F-13 guard: never leave the platform without an active admin.
        var removesAdministrator = user.IsActive && string.Equals(target.Role.Name, "Admin", StringComparison.Ordinal);
        await using var administratorSet = removesAdministrator
            ? await repo.LockAdministratorSetAsync(ct).ConfigureAwait(false)
            : null;
        if (removesAdministrator)
        {
            var admins = await repo.CountActiveAdminsAsync(ct).ConfigureAwait(false);
            if (admins <= 1)
                throw new BadRequestException("Cannot remove the last active administrator from the Admin role.");
        }

        user.UserRoles.Remove(target);
        await PersistRoleChangeAsync(user, ct).ConfigureAwait(false);
        return true;
    }

    private async Task PersistRoleChangeAsync(User user, CancellationToken ct)
    {
        user.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        RotateSecurityStamp(user);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        // Invalidate AFTER the commit - see UpdateUserAsync for why.
        InvalidateAuthzCaches(user);

        await audit.LogAsync("Updated", "User", user.Id, user.Username, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.User, user.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        await userNotifier.NotifyPermissionsChangedAsync(user.Id, PermissionChangeReasons.Roles, ct).ConfigureAwait(false);
    }

    // Owner decision 2026-10-28: every account that newly holds the site Admin role is audited and told by mail.
    // Auth sends the mail and audits its outcome; a mail that does not leave never undoes the grant.
    private async Task AnnounceSiteAdminAsync(User user, CancellationToken ct)
    {
        await audit.LogAsync("SiteAdminGranted", "User", user.Id, user.Username, ct).ConfigureAwait(false);
        await domainEvents.DispatchAsync(new SiteAdminGrantedEvent(user.Id, user.Username, user.Email), ct).ConfigureAwait(false);
    }

    // Rotates the security stamp (immediate JWT revocation). Part of the entity being persisted, so
    // this must run BEFORE SaveChangesAsync. Pair with InvalidateAuthzCaches AFTER the commit - see
    // that method's doc comment for why the two are not combined into a single pre-save call.
    private static void RotateSecurityStamp(User user) => user.SecurityStamp = Guid.NewGuid().ToString("N");

    // Drops the stamp cache entry and the authz role/org cache so a role or activation change takes
    // effect on the very next request. Must run AFTER SaveChangesAsync commits: evicting before the
    // commit opens a window where a concurrent read between the eviction and the commit re-populates
    // the cache with the OLD role set for the remainder of its TTL (~5 min) - a stale-privilege bug.
    private void InvalidateAuthzCaches(User user)
    {
        cache.Remove($"sec-stamp:{user.Id}");
        authz.InvalidateRoleCache(user.Username);
    }

    private static bool IsSiteRole(string roleName) =>
        string.Equals(roleName, RoleNames.Admin, StringComparison.Ordinal);

    // ADR-059: only Admin is held site-wide; any other role names an organization.
    private static void RefuseOrganizationRolesHeldSiteWide(IEnumerable<string> roleNames)
    {
        var refused = roleNames.Where(name => !IsSiteRole(name)).ToList();
        if (refused.Count > 0)
            throw new BadRequestException(
                $"Only the '{RoleNames.Admin}' role is held site-wide; give {string.Join(", ", refused)} for an organization.");
    }

    private static List<string> SiteRoles(User user) =>
        user.UserRoles.Where(item => IsSiteRole(item.Role.Name)).Select(item => item.Role.Name).ToList();

    private static List<OrganizationRoleAssignmentDto> OrganizationRoles(User user) =>
        user.OrganizationRoles
            .OrderBy(item => item.Organization?.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Role?.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new OrganizationRoleAssignmentDto
            {
                OrganizationId = item.OrganizationId,
                OrganizationName = item.Organization?.Name,
                Role = item.Role?.Name ?? string.Empty
            })
            .ToList();

    private sealed record RoleChange(bool HasChanged, bool WasAdmin, bool WillBeAdmin)
    {
        public bool LosingAdmin => WasAdmin && !WillBeAdmin;
    }

    private static UserDto MapToDto(User u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        Email = u.Email,
        IsActive = u.IsActive,
        TotpEnabled = u.TotpEnabled,
        MustChangePassword = u.MustChangePassword,
        Roles = SiteRoles(u),
        OrganizationRoles = OrganizationRoles(u),
        CreatedAt = u.CreatedAt,
        UpdatedAt = u.UpdatedAt,
        LastActivityAt = u.LastActivityAt
    };
}
