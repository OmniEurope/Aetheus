// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Npgsql;

namespace Aetheus.Back.Components.Auth;

/// <summary>The administrator bootstrap's lock (an <see cref="AppSetting"/> row the Settings API never writes) and
/// the site Admin role holders it looks at.</summary>
public interface ISiteAdminBootstrapRepository
{
    Task<bool> IsLockedAsync(CancellationToken ct = default);

    /// <summary>Whether any account, active or not, holds the site Admin role.</summary>
    Task<bool> AnyAdministratorAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes the lock with <paramref name="value"/> and, when <paramref name="grantUserId"/> is set, gives that
    /// account the Admin role in the same commit. False, with nothing written, when another request locked first:
    /// the lock's key is the row's primary key, so two simultaneous bootstraps cannot both win.
    /// </summary>
    Task<bool> TryLockAsync(string value, int? grantUserId, DateTime now, CancellationToken ct = default);
}

public sealed class SiteAdminBootstrapRepository(AppDbContext db) : ISiteAdminBootstrapRepository
{
    public Task<bool> IsLockedAsync(CancellationToken ct = default) =>
        db.AppSettings.AsNoTracking().AnyAsync(setting => setting.Key == SiteAdminBootstrap.MarkerKey, ct);

    public Task<bool> AnyAdministratorAsync(CancellationToken ct = default) =>
        db.UserRoles.AsNoTracking().AnyAsync(userRole => userRole.Role.Name == RoleNames.Admin, ct);

    public async Task<bool> TryLockAsync(string value, int? grantUserId, DateTime now, CancellationToken ct = default)
    {
        var marker = new AppSetting { Key = SiteAdminBootstrap.MarkerKey, Value = value, UpdatedAt = now };
        db.AppSettings.Add(marker);
        UserRole? grant = null;
        if (grantUserId is { } userId)
        {
            var adminRoleId = await db.Roles.AsNoTracking().Where(role => role.OrganizationId == null && role.Name == RoleNames.Admin)
                .Select(role => role.Id).SingleAsync(ct).ConfigureAwait(false);
            grant = new UserRole { UserId = userId, RoleId = adminRoleId };
            db.UserRoles.Add(grant);
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another request locked the bootstrap between the read and this write: it won, this one grants nothing.
            db.Entry(marker).State = EntityState.Detached;
            if (grant is not null) db.Entry(grant).State = EntityState.Detached;
            return false;
        }
    }
}
