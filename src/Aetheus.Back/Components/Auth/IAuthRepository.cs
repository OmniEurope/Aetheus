// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Auth;

public interface IAuthRepository
{
    Task<RegistrationToken?> FindValidRegistrationTokenAsync(string token, CancellationToken ct = default);

    /// <summary>Finds a server by hostname in any organization, retired servers included.</summary>
    Task<Server?> FindServerByHostnameAsync(string hostname, CancellationToken ct = default);

    /// <summary>PLAN-004 R-11: finds a server of <paramref name="organizationId"/> (retired included)
    /// that recorded this machine identity hash; <paramref name="hostname"/> breaks ties.</summary>
    Task<Server?> FindServerByMachineIdHashAsync(
        int organizationId, string machineIdHash, string hostname, CancellationToken ct = default);

    Task<User?> FindUserWithRolesAsync(string username, CancellationToken ct = default);
    Task<User?> FindUserByIdWithRolesAsync(int userId, CancellationToken ct = default);

    Task<bool> AnyUsersExistAsync(CancellationToken ct = default);

    Task AddUserAsync(User user, CancellationToken ct = default);

    void AddServer(Server server);

    void AddServerToken(ServerToken serverToken);

    /// <summary>
    /// Atomically consumes a one-use registration token, persists the server changes, revokes any
    /// previous active bearer, and emits the replacement bearer record.
    /// </summary>
    Task<bool> TryPersistServerEnrollmentAsync(
        int registrationTokenId,
        Server server,
        ServerToken serverToken,
        CancellationToken ct = default);

    void AddRegistrationToken(RegistrationToken token);

    Task<List<RegistrationToken>> GetRegistrationTokensAsync(CancellationToken ct = default);

    Task<RegistrationToken?> FindRegistrationTokenByIdAsync(int id, CancellationToken ct = default);

    Task<int?> ValidateServerTokenHashAsync(string tokenHash, CancellationToken ct = default);

    Task RevokeServerTokensAsync(int serverId, CancellationToken ct = default);

    /// <summary>Latest expiry among the server's still-active (non-revoked, unexpired) tokens, or null if none.</summary>
    Task<DateTime?> GetLatestActiveServerTokenExpiryAsync(int serverId, CancellationToken ct = default);

    /// <summary>Removes already-dead (revoked or expired) token rows for the server. Active tokens are untouched.</summary>
    Task<int> DeleteExpiredServerTokensAsync(int serverId, CancellationToken ct = default);

    Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default);

    Task<bool> ServerHasActiveTokensAsync(int serverId, CancellationToken ct = default);

    Task<bool> ConsumeRegistrationTokenAsync(int tokenId, int? serverId, CancellationToken ct = default);

    Task<ExternalLogin?> FindExternalLoginAsync(string provider, string providerSubjectId, CancellationToken ct = default);

    Task AddExternalLoginAsync(ExternalLogin externalLogin, CancellationToken ct = default);

    Task<string?> GetSecurityStampAsync(int userId, CancellationToken ct = default);

    Task BumpSecurityStampAsync(int userId, CancellationToken ct = default);

    // --- Lockout (F-011) ---
    /// <summary>
    /// Adds one failed attempt in the store itself and returns the count read back afterwards, so
    /// concurrent failures never overwrite each other's increment and the lockout threshold is
    /// decided on the stored value, not on the caller's snapshot.
    /// </summary>
    Task<int> IncrementFailedLoginAsync(int userId, CancellationToken ct = default);
    Task ResetFailedLoginAsync(int userId, CancellationToken ct = default);
    Task LockoutUserAsync(int userId, DateTime lockoutEndUtc, CancellationToken ct = default);
    Task ClearLockoutAsync(int userId, CancellationToken ct = default);

    // --- Refresh Tokens (F-012) ---
    Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct = default);
    Task<RefreshToken?> FindRefreshTokenByHashAsync(string tokenHash, CancellationToken ct = default);
    /// <summary>Untracked current state of one refresh token (re-read after a lost rotation race).</summary>
    Task<RefreshToken?> FindRefreshTokenStateAsync(int tokenId, CancellationToken ct = default);
    /// <summary>
    /// Revokes the token only while it is still active; false when it was already revoked (a concurrent
    /// rotation won, or a logout revoked it) or does not exist. Commits immediately.
    /// </summary>
    Task<bool> RevokeRefreshTokenAsync(int tokenId, int? replacedById, CancellationToken ct = default);
    Task RevokeAllUserRefreshTokensAsync(int userId, CancellationToken ct = default);
    Task DeleteExpiredRefreshTokensAsync(int userId, CancellationToken ct = default);
    Task<int> DeleteAllExpiredRefreshTokensAsync(DateTime utcNow, CancellationToken ct = default);

    // --- TOTP (F-010) ---
    /// <summary>
    /// The user, tracked for an update. It takes no row lock: concurrent recovery-code consumes are
    /// serialized by the conditional replace of <see cref="ReplaceTotpRecoveryCodesAsync"/> (BACK-011).
    /// </summary>
    Task<User?> FindUserByIdTrackedAsync(int userId, CancellationToken ct = default);
    /// <summary>Untracked current recovery-code JSON of one user (re-read after a lost consume race).</summary>
    Task<string?> GetTotpRecoveryCodesAsync(int userId, CancellationToken ct = default);
    /// <summary>
    /// Replaces the recovery-code JSON only while the stored value still equals
    /// <paramref name="expectedCodes"/>; false when a concurrent consume changed it first. Commits immediately.
    /// </summary>
    Task<bool> ReplaceTotpRecoveryCodesAsync(int userId, string expectedCodes, string newCodes, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
