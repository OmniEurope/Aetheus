// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Users;

/// <summary>R-1 (owner decision 2026-10-29): the first step of the administrator a fresh install seeds.</summary>
public interface IAccountSetupService
{
    /// <summary>Turns the caller's seeded administrator into its holder's own account: name, sign-in address and
    /// password all replaced at once. False when the current password is wrong; refused (400) when the caller has
    /// no setup pending, keeps the public default address or the password it has; 409 for a name or an address
    /// another account holds.</summary>
    Task<bool> SetUpOwnAccountAsync(SetUpOwnAccountRequest request, CancellationToken ct = default);
}

public sealed class AccountSetupService(
    IUserRepository repo,
    IHttpContextAccessor httpContextAccessor,
    IAuditService audit,
    IResourceAuthorizationService authz,
    IMemoryCache cache,
    TimeProvider timeProvider,
    IAdminChangeNotifier notifier,
    IDomainEventDispatcher domainEvents) : IAccountSetupService
{
    public async Task<bool> SetUpOwnAccountAsync(SetUpOwnAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The account is the caller's own, read from the token: the client never names a target.
        var callerName = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name);
        // The lookup by name reads untracked; the row written below is loaded tracked by its id.
        var caller = string.IsNullOrEmpty(callerName)
            ? null
            : await repo.FindByUsernameAsync(callerName, ct).ConfigureAwait(false);
        var user = caller is null ? null : await repo.FindUserAsync(caller.Id, ct).ConfigureAwait(false);
        if (user is null || !user.MustSetUpAccount)
            throw new BadRequestException("This account has no account setup pending.");
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return false;

        var username = (request.Username ?? string.Empty).Trim().ToLowerInvariant();
        var email = await RequireOwnAddressAsync(request.Email, user.Id, ct).ConfigureAwait(false);
        if (username.Length == 0)
            throw new BadRequestException("A name is required.");
        if (await repo.FindByUsernameAsync(username, ct).ConfigureAwait(false) is { } holder && holder.Id != user.Id)
            throw new ConflictException($"Username '{username}' is already taken.");
        // The seeded password came from the deployment configuration: it does not stay usable.
        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, user.PasswordHash))
            throw new BadRequestException("Choose a password other than the one of the installation.");

        var previousUsername = user.Username;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        user.Username = username;
        user.Email = email;
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.MustSetUpAccount = false;
        user.MustChangePassword = false;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.UpdatedAt = now;
        // Everything the seeded credentials opened ends with them: every token of the account, refresh and personal.
        var revokedTokens = await repo.RevokePersonalAccessTokensAsync(user.Id, now, ct).ConfigureAwait(false);
        await repo.RevokeRefreshTokensAsync(user.Id, now, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        // After the commit, as UserService does: an eviction before it lets a concurrent read cache the old state.
        cache.Remove($"sec-stamp:{user.Id}");
        authz.InvalidateRoleCache(previousUsername);
        authz.InvalidateRoleCache(user.Username);
        await audit.LogAsync("AccountSetUp", "User", user.Id,
            $"{previousUsername} -> {user.Username}: seeded administrator replaced by its holder's own name, address and password"
            + (revokedTokens > 0 ? $"; {revokedTokens} personal access token(s) revoked" : string.Empty),
            ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.User, user.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        await domainEvents.DispatchAsync(
            new UserSessionEndedEvent(user.Id, UserSessionEndReasons.PasswordChanged), ct).ConfigureAwait(false);
        return true;
    }

    // The sign-in address becomes the holder's: never the public default one, and held by no other account.
    private async Task<string> RequireOwnAddressAsync(string? requested, int userId, CancellationToken ct)
    {
        var email = UserEmail.Normalize(requested)
            ?? throw new BadRequestException("An e-mail address is required: it is the sign-in identifier.");
        if (string.Equals(email, Aetheus.Back.Data.SeededAdminEmail.Default, StringComparison.Ordinal))
            throw new BadRequestException("Choose your own e-mail address, not the default one of the installation.");
        if (await repo.EmailInUseAsync(email, userId, ct).ConfigureAwait(false))
            throw new ConflictException("This e-mail address is already used by another account.", UserErrorCodes.EmailInUse);
        return email;
    }
}
