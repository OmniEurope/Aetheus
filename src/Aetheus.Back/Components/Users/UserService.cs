// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Users;

public class UserService(IUserRepository repo, IHttpContextAccessor httpContextAccessor, IAuditService audit, IResourceAuthorizationService authz, IMemoryCache cache, TimeProvider timeProvider, IAdminChangeNotifier notifier, IUserChangeNotifier userNotifier, IDomainEventDispatcher domainEvents) : IUserService
{
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
            Roles = user.UserRoles.Select(ur => ur.Role.Name).ToList(),
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt
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

        var user = new User
        {
            Username = normalized,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Email = request.Email,
            IsActive = true,
            MustChangePassword = request.MustChangePassword
        };

        await repo.AddUserAsync(user, ct).ConfigureAwait(false);

        if (request.Roles.Count > 0)
        {
            var roles = await repo.GetRolesByNamesAsync(request.Roles, ct).ConfigureAwait(false);
            foreach (var role in roles)
                user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, Role = role });

            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var detail = await repo.GetUserDetailAsync(user.Id, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "User", user.Id, user.Username, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.User, user.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);
        return MapToDto(detail!);
    }

    public async Task<UserDto?> UpdateUserAsync(int id, UpdateUserRequest request, CancellationToken ct = default)
    {
        var user = await repo.GetUserDetailAsync(id, ct).ConfigureAwait(false);
        if (user is null) return null;
        var normalized = (request.Username ?? string.Empty).Trim().ToLowerInvariant();
        await EnsureUsernameAvailableAsync(id, user.Username, normalized, ct).ConfigureAwait(false);
        user.Username = normalized;
        user.Email = request.Email;
        var deactivating = user.IsActive && !request.IsActive;
        user.IsActive = request.IsActive;
        user.MustChangePassword = request.MustChangePassword;
        user.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        var requestedRolesList = request.Roles ?? [];
        var roleChange = DescribeRoleChange(user, requestedRolesList);
        await EnsureSecurityChangeAllowedAsync(user, roleChange, deactivating, ct).ConfigureAwait(false);
        if (roleChange.HasChanged)
            await ApplyRolesAsync(user, requestedRolesList, ct).ConfigureAwait(false);
        if (roleChange.HasChanged || deactivating) RotateSecurityStamp(user);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await PublishUserUpdateAsync(user, roleChange.HasChanged, deactivating, ct).ConfigureAwait(false);
        if (deactivating)
            await EndSessionAsync(user.Id, UserSessionEndReasons.Deactivated, ct).ConfigureAwait(false);
        return MapToDto(user);
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

    private static RoleChange DescribeRoleChange(User user, IReadOnlyCollection<string> requestedRoles)
    {
        var previous = user.UserRoles.Select(item => item.Role.Name).OrderBy(name => name).ToList();
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
        user.UserRoles.RemoveAll(userRole => !requestedRoleIds.Contains(userRole.RoleId));
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

    public async Task<bool> DeleteUserAsync(int id, CancellationToken ct = default)
    {
        var user = await repo.FindUserAsync(id, ct).ConfigureAwait(false);
        if (user is null) return false;

        // F-13: prevent self-deletion.
        var callerName = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name);
        if (callerName is not null && string.Equals(callerName, user.Username, StringComparison.Ordinal))
            throw new BadRequestException("You cannot delete your own account.");

        // F-13: prevent removal of the last active admin.
        var userDetail = await repo.GetUserDetailAsync(id, ct).ConfigureAwait(false);
        if (userDetail is not null && userDetail.IsActive && userDetail.UserRoles.Any(ur => ur.Role.Name == "Admin"))
        {
            var admins = await repo.CountActiveAdminsAsync(ct).ConfigureAwait(false);
            if (admins <= 1)
                throw new BadRequestException("Cannot delete the last active administrator.");
        }

        var name = user.Username;
        await repo.RemoveUserAsync(user, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "User", id, name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.User, id, EntityChangeOps.Deleted, ct).ConfigureAwait(false);
        await EndSessionAsync(id, UserSessionEndReasons.Deleted, ct).ConfigureAwait(false);
        return true;
    }

    // Recette R1-37: tells the modules holding per-user state in memory (the typed mailbox passwords)
    // that this user's session is over. In-process and awaited, so it is done when the request returns.
    private Task EndSessionAsync(int userId, string reason, CancellationToken ct) =>
        domainEvents.DispatchAsync(new UserSessionEndedEvent(userId, reason), ct);

    public async Task<bool> ChangePasswordAsync(int id, ChangeUserPasswordRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await repo.FindUserAsync(id, ct).ConfigureAwait(false);
        if (user is null) return false;

        // Determine if the caller is acting on themselves or on another user.
        // No HttpContext (e.g. background worker, integration tests) is treated as a system reset
        // and bypasses the self-service current-password check.
        var caller = httpContextAccessor.HttpContext?.User;
        var callerName = caller?.FindFirstValue(ClaimTypes.Name);
        var isSelf = callerName is not null && string.Equals(callerName, user.Username, StringComparison.Ordinal);
        var isAdmin = caller?.IsInRole("Admin") == true;
        var isSystem = caller is null;

        // Self-service changes always require the current password.
        // Admin / system-driven resets are allowed without it.
        if (isSelf)
        {
            if (string.IsNullOrEmpty(request.CurrentPassword) ||
                !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
                return false;
        }
        else if (!isAdmin && !isSystem)
        {
            return false;
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        // A self-service change satisfies a pending forced-change requirement: clear the flag so the
        // next login no longer routes the user onto the mandatory change-password screen. An admin
        // resetting another user's password re-arms it (S-FEAT-UE4K) so that user must pick their own.
        if (isSelf)
            user.MustChangePassword = false;
        else if (isAdmin)
            user.MustChangePassword = true;
        // A reset by someone else also ends the personal access tokens: the security-stamp rotation above
        // revokes the JWTs, but a PAT is checked against its own row, not the stamp (SITE-SEC-001).
        var revokedTokens = isSelf
            ? 0
            : await repo.RevokePersonalAccessTokensAsync(user.Id, user.UpdatedAt, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
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
        return true;
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

    public async Task AssignRoleAsync(int userId, int roleId, CancellationToken ct = default)
    {
        var user = await repo.GetUserDetailAsync(userId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"User {userId} not found.");
        var role = await repo.FindRoleAsync(roleId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Role {roleId} not found.");

        if (user.UserRoles.Any(ur => ur.RoleId == roleId))
            throw new ConflictException($"User '{user.Username}' already holds role '{role.Name}'.");

        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await PersistRoleChangeAsync(user, ct).ConfigureAwait(false);
    }

    public async Task<bool> UnassignRoleAsync(int userId, int roleId, CancellationToken ct = default)
    {
        var user = await repo.GetUserDetailAsync(userId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"User {userId} not found.");

        var target = user.UserRoles.FirstOrDefault(ur => ur.RoleId == roleId);
        if (target is null) return false;

        // Mirror DeleteUserAsync's F-13 guard: never leave the platform without an active admin.
        if (user.IsActive && string.Equals(target.Role.Name, "Admin", StringComparison.Ordinal))
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
        Roles = u.UserRoles.Select(ur => ur.Role.Name).ToList(),
        CreatedAt = u.CreatedAt,
        UpdatedAt = u.UpdatedAt
    };
}
