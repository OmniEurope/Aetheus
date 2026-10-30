// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Auth;

/// <summary>
/// Owner decision 2026-10-28, after WordMorrow's configured administrator addresses: the account signing in with
/// the address of <c>Auth:AdminEmail</c> becomes site Admin at its first sign-in (or first password set from a
/// mailed link), and is told by mail. It happens once: the grant writes a lock in the database, and so does the
/// first start that finds an administrator already there (an existing platform, or the seeded administrator of a
/// new one) without granting anything. Once locked, changing the configured address makes nobody Admin.
/// </summary>
public interface ISiteAdminBootstrap
{
    /// <summary>At startup: locks the bootstrap, granting nothing, when any account already holds the Admin role.</summary>
    Task LockIfAdministratorExistsAsync(CancellationToken ct = default);

    /// <summary>
    /// After a proof of access to the account: makes it site Admin when it holds the configured address, the
    /// bootstrap is not locked and no account holds the Admin role, then locks and mails it. True when granted.
    /// </summary>
    Task<bool> TryGrantAsync(int userId, string username, string? email, CancellationToken ct = default);
}

public sealed class SiteAdminBootstrap(
    ISiteAdminBootstrapRepository repo,
    IConfiguration configuration,
    IAuditService audit,
    ISiteAdminGrantMail mail,
    IResourceAuthorizationService authz,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<SiteAdminBootstrap> logger) : ISiteAdminBootstrap
{
    /// <summary>The <see cref="Data.Entities.AppSetting"/> key of the lock. Not in the Settings API's allow-list, so
    /// no administrator can clear it through the application.</summary>
    public const string MarkerKey = "Auth:SiteAdminBootstrap";

    internal const string LockedOnExistingAdministrator = "ExistingAdministrator";

    public async Task LockIfAdministratorExistsAsync(CancellationToken ct = default)
    {
        if (await repo.IsLockedAsync(ct).ConfigureAwait(false) || !await repo.AnyAdministratorAsync(ct).ConfigureAwait(false))
            return;
        await LockWithoutGrantAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> TryGrantAsync(int userId, string username, string? email, CancellationToken ct = default)
    {
        // Only an address set explicitly: the default one of a fresh install is public knowledge.
        var configured = UserEmail.Normalize(configuration["Auth:AdminEmail"]);
        if (configured is null || !string.Equals(UserEmail.Normalize(email), configured, StringComparison.Ordinal))
            return false;
        if (await repo.IsLockedAsync(ct).ConfigureAwait(false))
            return false;
        if (await repo.AnyAdministratorAsync(ct).ConfigureAwait(false))
        {
            await LockWithoutGrantAsync(ct).ConfigureAwait(false);
            return false;
        }

        if (!await repo.TryLockAsync($"Granted:{userId}", userId, timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false))
            return false;
        // After the commit, so no request in between caches the former roles again (see UserService).
        cache.Remove($"sec-stamp:{userId}");
        authz.InvalidateRoleCache(username);
        logger.LogWarning("[SiteAdmin] User {UserId} made site Admin by the bootstrap of Auth:AdminEmail; the bootstrap is now locked", userId);
        await audit.LogAsync("SiteAdminGranted", "User", userId, $"{username}: bootstrap of the configured administrator address", ct).ConfigureAwait(false);
        await mail.SendAsync(userId, username, email, ct).ConfigureAwait(false);
        return true;
    }

    private async Task LockWithoutGrantAsync(CancellationToken ct)
    {
        if (!await repo.TryLockAsync(LockedOnExistingAdministrator, null, timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false))
            return;
        logger.LogInformation("[SiteAdmin] An administrator already exists: the administrator bootstrap is locked without granting anything");
        await audit.LogAsync("SiteAdminBootstrapLocked", "Authentication", null, "an administrator already exists: nothing granted", ct).ConfigureAwait(false);
    }
}
