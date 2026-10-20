// SPDX-License-Identifier: EUPL-1.2
using System.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Auth;

public class AuthRepository(AppDbContext db, TimeProvider timeProvider) : IAuthRepository
{
    public async Task<RegistrationToken?> FindValidRegistrationTokenAsync(string token, CancellationToken ct = default)
    {
        return await db.RegistrationTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Token == token
                                      && !t.IsUsed
                                      && t.ExpiresAt > timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
    }

    // Include retired servers in enrollment matching to revive the same row and preserve links.
    public async Task<Server?> FindServerByHostnameAsync(string hostname, CancellationToken ct = default)
    {
        return await db.Servers
            .IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired])
            .FirstOrDefaultAsync(s => s.Hostname == hostname, ct).ConfigureAwait(false);
    }

    public async Task<Server?> FindServerByMachineIdHashAsync(
        int organizationId, string machineIdHash, string hostname, CancellationToken ct = default)
    {
        // A hash is not a key (cloned images share one), so several rows may carry it: prefer the one
        // that also has the reported hostname, then an active one, then the oldest, deterministically.
        return await db.Servers
            .IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired])
            .Where(s => s.OrganizationId == organizationId && s.MachineIdHash == machineIdHash)
            .OrderBy(s => s.Hostname == hostname ? 0 : 1)
            .ThenBy(s => s.DeletedAt == null ? 0 : 1)
            .ThenBy(s => s.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<User?> FindUserWithRolesAsync(string username, CancellationToken ct = default)
    {
        return await db.Users.FindByUsernameWithRolesAsync(username, ct).ConfigureAwait(false);
    }

    public async Task<User?> FindUserByIdWithRolesAsync(int userId, CancellationToken ct = default)
    {
        return await db.Users
            .AsNoTracking()
            .IncludeRoles()
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> AnyUsersExistAsync(CancellationToken ct = default)
    {
        return await db.Users.AnyAsync(ct).ConfigureAwait(false);
    }

    public async Task AddUserAsync(User user, CancellationToken ct = default)
    {
        db.Users.Add(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public void AddServer(Server server)
    {
        db.Servers.Add(server);
    }

    public void AddServerToken(ServerToken serverToken)
    {
        db.ServerTokens.Add(serverToken);
    }

    public async Task<bool> TryPersistServerEnrollmentAsync(
        int registrationTokenId,
        Server server,
        ServerToken serverToken,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!db.Database.IsRelational())
        {
            var registrationToken = await db.RegistrationTokens
                .FirstOrDefaultAsync(
                    token => token.Id == registrationTokenId
                        && !token.IsUsed
                        && token.ExpiresAt > now,
                    ct)
                .ConfigureAwait(false);
            if (registrationToken is null)
                return false;

            registrationToken.IsUsed = true;
            registrationToken.UsedByServer = server;
            if (server.Id == 0 && db.Entry(server).State == EntityState.Detached)
                db.Servers.Add(server);

            if (server.Id != 0)
            {
                var activeTokens = await db.ServerTokens
                    .Where(token => token.ServerId == server.Id
                        && !token.IsRevoked
                        && token.ExpiresAt > now)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                foreach (var activeToken in activeTokens)
                    activeToken.IsRevoked = true;
            }

            db.ServerTokens.Add(serverToken);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }

        // The non-relational branch above is the tracked-entity fallback for every bulk update below.
        // Keep this invariant explicit beside the relational transaction so future additions remain guarded.
        if (!db.Database.IsRelational())
            throw new InvalidOperationException("The relational enrollment path requires a relational database.");

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            .ConfigureAwait(false);
        try
        {
            // Claim the registration token before inserting any server state. The compare-and-set
            // row update serializes concurrent enrollments without relying on an earlier read.
            var consumed = await db.RegistrationTokens
                .Where(token => token.Id == registrationTokenId
                    && !token.IsUsed
                    && token.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(token => token.IsUsed, true)
                        .SetProperty(token => token.UsedByServerId, (int?)null),
                    ct)
                .ConfigureAwait(false);
            if (consumed == 0)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            if (server.Id == 0 && db.Entry(server).State == EntityState.Detached)
                db.Servers.Add(server);
            if (server.Id != 0)
            {
                await db.ServerTokens
                    .Where(token => token.ServerId == server.Id
                        && !token.IsRevoked
                        && token.ExpiresAt > now)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(token => token.IsRevoked, true),
                        ct)
                    .ConfigureAwait(false);
            }

            db.ServerTokens.Add(serverToken);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await db.RegistrationTokens
                .Where(token => token.Id == registrationTokenId && token.IsUsed)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(token => token.UsedByServerId, server.Id),
                    ct)
                .ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public void AddRegistrationToken(RegistrationToken token)
    {
        db.RegistrationTokens.Add(token);
    }

    public async Task<List<RegistrationToken>> GetRegistrationTokensAsync(CancellationToken ct = default)
    {
        return await db.RegistrationTokens
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<RegistrationToken?> FindRegistrationTokenByIdAsync(int id, CancellationToken ct = default)
    {
        return await db.RegistrationTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<int?> ValidateServerTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        // Check DeletedAt as well as revoked tokens to prevent an in-flight heartbeat from renewing
        // credentials after retirement.
        var serverToken = await db.ServerTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash
                                      && !t.IsRevoked
                                      && t.ExpiresAt > timeProvider.GetUtcNow().UtcDateTime
                                      && t.Server.DeletedAt == null, ct).ConfigureAwait(false);
        return serverToken?.ServerId;
    }

    public async Task RevokeServerTokensAsync(int serverId, CancellationToken ct = default)
    {
        var activeTokens = await db.ServerTokens
            .Where(t => t.ServerId == serverId && !t.IsRevoked)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var token in activeTokens)
            token.IsRevoked = true;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<DateTime?> GetLatestActiveServerTokenExpiryAsync(int serverId, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return db.ServerTokens
            .AsNoTracking()
            .Where(t => t.ServerId == serverId && !t.IsRevoked && t.ExpiresAt > now)
            .OrderByDescending(t => t.ExpiresAt)
            .Select(t => (DateTime?)t.ExpiresAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int> DeleteExpiredServerTokensAsync(int serverId, CancellationToken ct = default)
    {
        // Load + RemoveRange (not ExecuteDelete): the dead set per server is tiny
        // and this stays provider-agnostic (EF InMemory, used by integration
        // tests, does not support ExecuteDelete). Flushed by the caller's
        // SaveChangesAsync together with the freshly-minted token (atomic).
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var dead = await db.ServerTokens
            .Where(t => t.ServerId == serverId && (t.IsRevoked || t.ExpiresAt <= now))
            .ToListAsync(ct).ConfigureAwait(false);
        if (dead.Count == 0) return 0;
        db.ServerTokens.RemoveRange(dead);
        return dead.Count;
    }

    public async Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default)
    {
        return await db.Servers.AnyAsync(s => s.Id == serverId, ct).ConfigureAwait(false);
    }

    public async Task<bool> ServerHasActiveTokensAsync(int serverId, CancellationToken ct = default)
    {
        return await db.ServerTokens
            .AnyAsync(t => t.ServerId == serverId && !t.IsRevoked && t.ExpiresAt > timeProvider.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> ConsumeRegistrationTokenAsync(int tokenId, int? serverId, CancellationToken ct = default)
    {
        var token = await db.RegistrationTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && !t.IsUsed, ct)
            .ConfigureAwait(false);

        if (token is null)
            return false;

        token.IsUsed = true;
        token.UsedByServerId = serverId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<ExternalLogin?> FindExternalLoginAsync(string provider, string providerSubjectId, CancellationToken ct = default)
    {
        return await db.ExternalLogins
            .AsNoTracking()
            .Include(e => e.User).ThenInclude(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(e => e.Provider == provider && e.ProviderSubjectId == providerSubjectId, ct)
            .ConfigureAwait(false);
    }

    public async Task AddExternalLoginAsync(ExternalLogin externalLogin, CancellationToken ct = default)
    {
        db.ExternalLogins.Add(externalLogin);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetSecurityStampAsync(int userId, CancellationToken ct = default)
    {
        return await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.SecurityStamp)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task BumpSecurityStampAsync(int userId, CancellationToken ct = default)
        => await UpdateUserAsync(
            userId, user => user.SecurityStamp = Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);

    // --- Lockout (F-011) ---

    public async Task<int> IncrementFailedLoginAsync(int userId, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (db.Database.IsRelational())
        {
            // BACK-010: the increment happens in the UPDATE, so concurrent failures each add one
            // instead of all writing "snapshot + 1". The value read back afterwards includes this
            // request's increment, so the last of N concurrent failures always sees all N.
            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1)
                        .SetProperty(u => u.UpdatedAt, now),
                    ct)
                .ConfigureAwait(false);
            return await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.FailedLoginCount)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null) return 0;
        user.FailedLoginCount++;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user.FailedLoginCount;
    }

    public async Task ResetFailedLoginAsync(int userId, CancellationToken ct = default)
        => await UpdateUserAsync(userId, ClearLockout, ct).ConfigureAwait(false);

    public async Task LockoutUserAsync(int userId, DateTime lockoutEndUtc, CancellationToken ct = default)
        => await UpdateUserAsync(userId, user => user.LockoutEndUtc = lockoutEndUtc, ct).ConfigureAwait(false);

    public async Task ClearLockoutAsync(int userId, CancellationToken ct = default)
        => await UpdateUserAsync(userId, ClearLockout, ct).ConfigureAwait(false);

    private async Task UpdateUserAsync(int userId, Action<User> update, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null) return;
        update(user);
        user.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static void ClearLockout(User user)
    {
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
    }

    // --- Refresh Tokens (F-012) ---

    public async Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct = default)
    {
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<RefreshToken?> FindRefreshTokenByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        return await db.RefreshTokens
            .Include(r => r.User).ThenInclude(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct)
            .ConfigureAwait(false);
    }

    public async Task<RefreshToken?> FindRefreshTokenStateAsync(int tokenId, CancellationToken ct = default) =>
        await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(r => r.Id == tokenId, ct).ConfigureAwait(false);

    public async Task<bool> RevokeRefreshTokenAsync(int tokenId, int? replacedById, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (db.Database.IsRelational())
        {
            // Compare-and-set on "still active", so of two concurrent rotations of one token
            // exactly one revokes it; the row lock makes the later UPDATE re-check and match
            // nothing.
            var revoked = await db.RefreshTokens
                .Where(r => r.Id == tokenId && r.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(r => r.RevokedAt, (DateTime?)now)
                        .SetProperty(r => r.ReplacedById, replacedById),
                    ct)
                .ConfigureAwait(false);
            return revoked == 1;
        }

        var token = await db.RefreshTokens.FirstOrDefaultAsync(r => r.Id == tokenId, ct).ConfigureAwait(false);
        if (token is null || token.RevokedAt is not null) return false;
        token.RevokedAt = now;
        token.ReplacedById = replacedById;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task RevokeAllUserRefreshTokensAsync(int userId, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var active = await db.RefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null && r.ExpiresAt > now)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in active)
            t.RevokedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteExpiredRefreshTokensAsync(int userId, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var graceStart = RefreshTokenRetention.RevokedCutoff(now);
        var dead = db.RefreshTokens
            .Where(r => r.UserId == userId && (r.ExpiresAt <= now || (r.RevokedAt != null && r.RevokedAt < graceStart)));
        if (db.Database.IsRelational())
        {
            // Use idempotent set-based deletion so concurrent refresh requests cannot stage
            // conflicting tracked deletes.
            await dead.ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return;
        }

        var staged = await dead.ToListAsync(ct).ConfigureAwait(false);
        if (staged.Count > 0)
            db.RefreshTokens.RemoveRange(staged);
        // Non-relational store: the caller's SaveChangesAsync flushes.
    }

    public async Task<int> DeleteAllExpiredRefreshTokensAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var grace = RefreshTokenRetention.RevokedCutoff(utcNow);
        var deadQuery = db.RefreshTokens
            .Where(r => r.ExpiresAt <= utcNow || (r.RevokedAt != null && r.RevokedAt < grace));
        if (db.Database.IsRelational())
            return await deadQuery.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var dead = await deadQuery.ToListAsync(ct).ConfigureAwait(false);
        if (dead.Count == 0)
            return 0;
        db.RefreshTokens.RemoveRange(dead);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return dead.Count;
    }

    // --- TOTP (F-010) ---

    public async Task<User?> FindUserByIdTrackedAsync(int userId, CancellationToken ct = default)
    {
        return await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);
    }

    public async Task<string?> GetTotpRecoveryCodesAsync(int userId, CancellationToken ct = default) =>
        await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.TotpRecoveryCodes)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<bool> ReplaceTotpRecoveryCodesAsync(int userId, string expectedCodes, string newCodes, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (db.Database.IsRelational())
        {
            // BACK-011: compare-and-set on the JSON that was read, so of two concurrent consumes of
            // one code exactly one rewrites the list; the row lock makes the later UPDATE re-check
            // the predicate and match nothing.
            var replaced = await db.Users
                .Where(u => u.Id == userId && u.TotpRecoveryCodes == expectedCodes)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(u => u.TotpRecoveryCodes, newCodes)
                        .SetProperty(u => u.UpdatedAt, now),
                    ct)
                .ConfigureAwait(false);
            return replaced == 1;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null || user.TotpRecoveryCodes != expectedCodes) return false;
        user.TotpRecoveryCodes = newCodes;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
