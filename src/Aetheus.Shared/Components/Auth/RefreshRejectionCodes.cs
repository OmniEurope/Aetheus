// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Auth;

/// <summary>Machine-readable rejection codes for <c>POST api/auth/token/refresh</c> let clients
/// explain why a session ended without disclosing token details.</summary>
public static class RefreshRejectionCodes
{
    /// <summary>No such token: never issued here, purged, or the database was restored or reset.</summary>
    public const string UnknownToken = "refresh_unknown";

    /// <summary>An already-rotated token reused outside the grace window; the whole chain is revoked.</summary>
    public const string Replay = "refresh_replay";

    /// <summary>The token outlived its lifetime.</summary>
    public const string Expired = "refresh_expired";

    /// <summary>The account was deactivated or deleted.</summary>
    public const string UserInactive = "user_inactive";
}

/// <summary>Reasons for an unsolicited client session end, reported to <c>POST
/// api/auth/session-ended</c>. Includes refresh rejections and client-observed failures.</summary>
public static class SessionEndReasons
{
    /// <summary>The refresh endpoint refused without a code this client knows (a 400 or 403, an older server).</summary>
    public const string RefreshRejected = "refresh_rejected";

    /// <summary>The legacy renew endpoint (tokens without a refresh token) refused.</summary>
    public const string RenewRejected = "renew_rejected";

    /// <summary>At start-up the stored access token had expired and no refresh token was stored.</summary>
    public const string NoRefreshTokenAtStartup = "no_refresh_token_at_startup";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        RefreshRejectionCodes.UnknownToken,
        RefreshRejectionCodes.Replay,
        RefreshRejectionCodes.Expired,
        RefreshRejectionCodes.UserInactive,
        RefreshRejected,
        RenewRejected,
        NoRefreshTokenAtStartup
    };

    /// <summary>A known reason for a refresh refusal code, <see cref="RefreshRejected"/> for anything else.</summary>
    public static string ForRefreshRejection(string? code) =>
        code is not null && All.Contains(code) && code != NoRefreshTokenAtStartup && code != RenewRejected
            ? code
            : RefreshRejected;
}
