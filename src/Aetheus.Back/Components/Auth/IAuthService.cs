// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Auth;

/// <summary>A refresh: the new tokens, or the reason it was refused (exactly one of the two).</summary>
public sealed record RefreshTokenOutcome(LoginResponse? Response, string? RejectionCode)
{
    public static RefreshTokenOutcome Rejected(string code) => new(null, code);
}

public interface IAuthService
{
    Task<bool> IsRegistrationTokenValidAsync(string token, CancellationToken ct = default);
    Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<RegistrationTokenDto> CreateRegistrationTokenAsync(CreateRegistrationTokenRequest request, CancellationToken ct = default);
    Task<List<RegistrationTokenDto>> GetRegistrationTokensAsync(CancellationToken ct = default);
    Task<RegistrationTokenDto?> GetRegistrationTokenAsync(int id, CancellationToken ct = default);
    Task<int?> ValidateServerTokenAsync(string token, CancellationToken ct = default);
    Task<ServerRegistrationResponse?> RotateAgentTokenAsync(int serverId, CancellationToken ct = default);

    /// <summary>
    /// If the server's freshest active agent token is within the renewal window,
    /// mints and persists a new one (WITHOUT revoking the old - fail-safe) and
    /// returns it for the agent to adopt. Returns null when no renewal is due.
    /// </summary>
    Task<ServerHeartbeatResponseDto?> MaybeRenewAgentTokenAsync(int serverId, CancellationToken ct = default);
    Task<LoginResponse?> HandleExternalLoginAsync(string provider, string subjectId, string? displayName, string? email, CancellationToken ct = default);
    Task<bool> IsSecurityStampValidAsync(int userId, string stamp, CancellationToken ct = default);
    Task BumpSecurityStampAsync(int userId, CancellationToken ct = default);
    Task<LoginResponse?> RenewTokenAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Recette R1-37: the signed-in user logs out. Announces the end of their session in-process so the
    /// modules holding per-user state in memory drop it now (the typed mailbox passwords); revokes no token.
    /// </summary>
    Task LogoutAsync(int userId, CancellationToken ct = default);

    // --- Refresh Token Rotation (F-012) ---
    Task<LoginResponse?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>The refresh, with the <c>RefreshRejectionCodes</c> reason when it is refused (PLAN-005 lot 9 / D48).</summary>
    Task<RefreshTokenOutcome> RefreshTokenWithReasonAsync(string refreshToken, CancellationToken ct = default);

    // --- TOTP 2FA (F-010) ---
    Task<TotpSetupResponse> SetupTotpAsync(int userId, CancellationToken ct = default);
    Task<bool> VerifyAndEnableTotpAsync(int userId, string code, CancellationToken ct = default);
    Task<bool> DisableTotpAsync(int userId, string password, CancellationToken ct = default);

    // --- Lockout (F-011) ---
    Task<bool> UnlockUserAsync(int userId, CancellationToken ct = default);

    /// <summary>Validates username/password for Basic-auth flows (e.g. Git Smart HTTP).</summary>
    Task<bool> ValidateBasicAuthAsync(string username, string password, CancellationToken ct = default);
}
