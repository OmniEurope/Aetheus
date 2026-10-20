// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aetheus.Front.Components.Shared;

// NOTE: JWT is stored in localStorage for Blazor WASM compatibility.
// HttpOnly cookies are not accessible from WASM; this is the standard Blazor WASM approach.
// XSS mitigation relies on CSP headers + input sanitization rather than cookie-based storage.
public class AuthStateProvider(IJSRuntime js, ILogger<AuthStateProvider> logger)
{
    private const string TokenKey = StorageKeys.AuthToken;
    private const string RefreshTokenKey = StorageKeys.RefreshToken;

    // Renew the token when fewer than this many minutes are left on the lifetime.
    // Chosen to be longer than typical request bursts so we renew at most once per session minute.
    private static readonly TimeSpan RenewalThreshold = TimeSpan.FromMinutes(5);

    // Custom claim minted by the backend (AetheusClaimTypes.MustChangePassword) - survives token
    // renewal/refresh so the forced change-password gate holds across a full-page reload.
    private const string MustChangePasswordClaim = "aetheus:mcp";

    // The opaque audience identifier minted by the backend (AetheusClaimTypes.AnalyticsVisitor).
    private const string AnalyticsVisitorClaim = "aetheus:avid";

    public string? Token { get; private set; }
    public string? RefreshToken { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(Token);

    /// <summary>
    /// True only when a non-expired access token is currently held. Unlike <see cref="IsAuthenticated"/>
    /// - which stays true for an expired-but-present token so a refresh can recover the session on a
    /// full-page reload - this reflects whether the token would actually be accepted right now. The
    /// 401 recovery path in <see cref="AuthDelegatingHandler"/> uses it before retrying with a freshly
    /// renewed token. Local expiry alone never decides whether the session must be cleared; only an
    /// explicit rejection from the renewal endpoint does.
    /// </summary>
    public bool HasValidToken => !string.IsNullOrEmpty(Token) && !IsTokenExpired(Token);
    public string? Username { get; private set; }

    /// <summary>The database id of the signed-in user (the token subject); null for an identity without a
    /// user row, such as the bootstrap administrator, and when signed out.</summary>
    public int? UserId { get; private set; }
    public List<string> Roles { get; private set; } = [];
    public bool IsAdmin => Roles.Contains("Admin", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True while the authenticated user is required to change their password before using the app.
    /// Read from the JWT so it survives reloads; cleared once a fresh token (without the claim) is
    /// issued after the change. <c>MainLayout</c> gates all navigation onto the change-password screen.
    /// </summary>
    public bool MustChangePassword { get; private set; }

    public event Action? OnAuthStateChanged;

    /// <summary>The tab's one token renewal, shared by every <see cref="AuthDelegatingHandler"/> (R1-89).</summary>
    internal TokenRenewalGate RenewalGate { get; } = new();

    /// <summary>
    /// Raised only when the token-renewal endpoint definitively rejects the session. Transient
    /// deployment, network and server failures retain the local session. Subscribers - typically
    /// <c>MainLayout</c> - should navigate the user to the login screen. Keeping the redirect here
    /// (out of <c>AuthDelegatingHandler</c>) avoids racy
    /// in-handler navigation and the redirect loops it can cause when multiple parallel requests
    /// each trigger their own <c>NavigateTo</c> call. (F-41)
    /// </summary>
    public event Action? OnNeedsLogin;

    public void NotifyNeedsLogin() => OnNeedsLogin?.Invoke();

    /// <summary>Keep the involuntary session-end reason until the next sign-in so the layout can
    /// explain and report it; voluntary logout leaves it unset.</summary>
    public string? LastSessionEndReason { get; private set; }

    /// <summary>
    /// The pending session-end reason, handed out once: several requests refused in parallel end the
    /// same session, and the user is told (and the server informed) once.
    /// </summary>
    public string? TakeSessionEndReason()
    {
        var reason = LastSessionEndReason;
        LastSessionEndReason = null;
        return reason;
    }

    /// <summary>Ends the session for <paramref name="reason"/>: the involuntary counterpart of <see cref="LogoutAsync"/>.</summary>
    public async Task EndSessionAsync(string reason)
    {
        LastSessionEndReason = reason;
        await LogoutAsync();
    }

    /// <summary>Reread browser storage before renewing because another tab may have rotated the
    /// refresh token; adopt that refresh token and a newer valid access token to avoid triggering
    /// replay revocation with a stale copy.</summary>
    public async Task ReloadFromStorageAsync()
    {
        try
        {
            var storedRefresh = await js.InvokeAsync<string?>("localStorage.getItem", RefreshTokenKey);
            if (!string.IsNullOrEmpty(storedRefresh))
                RefreshToken = storedRefresh;
            var storedToken = await js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
            if (!string.IsNullOrEmpty(storedToken) && !string.Equals(storedToken, Token, StringComparison.Ordinal)
                && !IsTokenExpired(storedToken))
                AdoptToken(storedToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to re-read the stored tokens; keeping the ones in memory");
        }
    }

    /// <summary>
    /// D50: the <c>storage</c> event of another tab. Only a new value is adopted; a removal is not
    /// (another tab's sign-out is its own business, and this tab's tokens fail on their own if revoked).
    /// </summary>
    [JSInvokable]
    public void OnStorageChanged(string key, string? newValue)
    {
        if (string.IsNullOrEmpty(newValue)) return;
        if (key == RefreshTokenKey)
            RefreshToken = newValue;
        else if (key == TokenKey && !string.Equals(newValue, Token, StringComparison.Ordinal) && !IsTokenExpired(newValue))
        {
            AdoptToken(newValue);
            OnAuthStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Web analytics asks only whether the visitor is signed in (layout.js,
    /// <c>Aetheus.analyticsSignedIn</c>): a yes/no, never the token or the account.
    /// </summary>
    [JSInvokable]
    public bool IsSignedInForAnalytics() => HasValidToken;

    /// <summary>Use the backend-minted opaque analytics claim so distinct accounts behind one
    /// network remain distinguishable without exposing account names or tokens; return null when
    /// signed out.</summary>
    [JSInvokable]
    public string? VisitorForAnalytics() => HasValidToken ? _analyticsVisitor : null;

    private string? _analyticsVisitor;

    /// <summary>Tells the measurement that the visitor changed (sign-in, sign-out, another tab).</summary>
    private void NotifyAnalyticsIdentity() => _ = NotifyAnalyticsIdentityAsync();

    private async Task NotifyAnalyticsIdentityAsync()
    {
        try
        {
            await js.InvokeVoidAsync("Aetheus.analyticsIdentityChanged");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            // Mandatory: the measurement must never break a sign-in or a sign-out.
            logger.LogDebug(ex, "Audience measurement not told of the identity change");
        }
    }

    private DotNetObjectReference<AuthStateProvider>? _storageWatch;

    /// <summary>D50: listen to the other tabs' writes (idempotent).</summary>
    public async Task WatchStorageAsync()
    {
        if (_storageWatch is not null) return;
        _storageWatch = DotNetObjectReference.Create(this);
        try
        {
            await js.InvokeVoidAsync("Aetheus.watchAuthStorage", _storageWatch, TokenKey, RefreshTokenKey);
            await NotifyAnalyticsIdentityAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not watch the other tabs' token writes");
        }
    }

    private void AdoptToken(string token)
    {
        Token = token;
        Roles = ParseRoles(token);
        Username = ParseClaim(token, "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", "unique_name");
        UserId = ParseUserId(token);
        MustChangePassword = ParseClaim(token, MustChangePasswordClaim) == "true";
        _analyticsVisitor = ParseClaim(token, AnalyticsVisitorClaim);
        NotifyAnalyticsIdentity();
    }

    /// <summary>
    /// True when the current token expires within <see cref="RenewalThreshold"/>
    /// OR is already expired but a refresh token is available for recovery.
    /// This ensures proactive renewal on full-page reloads where the access token
    /// may have expired while the refresh token is still valid.
    /// </summary>
    public bool ShouldRenew()
    {
        if (string.IsNullOrEmpty(Token)) return false;
        var expiry = GetTokenExpiry(Token);
        if (expiry is null) return false;
        var remaining = expiry.Value - DateTimeOffset.UtcNow;
        // Already expired - can still recover if we have a refresh token.
        if (remaining <= TimeSpan.Zero)
            return !string.IsNullOrEmpty(RefreshToken);
        return remaining <= RenewalThreshold;
    }

    public async Task InitializeAsync()
    {
        try
        {
            Token = await js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
            RefreshToken = await js.InvokeAsync<string?>("localStorage.getItem", RefreshTokenKey);
            if (Token is not null && IsTokenExpired(Token))
            {
                // Don't logout immediately - the refresh token may still be valid.
                // AuthDelegatingHandler will attempt a refresh on the next request.
                if (string.IsNullOrEmpty(RefreshToken))
                    await EndSessionAsync(SessionEndReasons.NoRefreshTokenAtStartup);
            }
            if (Token is not null)
            {
                Roles = ParseRoles(Token);
                Username = ParseClaim(Token, "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", "unique_name");
                UserId = ParseUserId(Token);
                MustChangePassword = ParseClaim(Token, MustChangePasswordClaim) == "true";
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to initialize auth state from localStorage");
            Token = null;
            RefreshToken = null;
            Roles = [];
        }
    }

    public async Task LoginAsync(string token, string? refreshToken = null)
    {
        AdoptToken(token);
        LastSessionEndReason = null;
        await js.InvokeVoidAsync("localStorage.setItem", TokenKey, token);
        if (refreshToken is not null)
        {
            RefreshToken = refreshToken;
            await js.InvokeVoidAsync("localStorage.setItem", RefreshTokenKey, refreshToken);
        }
        OnAuthStateChanged?.Invoke();
    }

    public async Task LogoutAsync()
    {
        Token = null;
        RefreshToken = null;
        Username = null;
        UserId = null;
        Roles = [];
        MustChangePassword = false;
        _analyticsVisitor = null;
        await js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
        await js.InvokeVoidAsync("localStorage.removeItem", RefreshTokenKey);
        await NotifyAnalyticsIdentityAsync();
        OnAuthStateChanged?.Invoke();
    }

    private List<string> ParseRoles(string token)
    {
        // JWTs may ship the role claim under either the long SOAP-style URI
        // (ClaimTypes.Role) or the short JWT form ("role"), depending on how
        // JwtSecurityTokenHandler.OutboundClaimTypeMap is configured on the
        // server. Accept both so IsAdmin stays correct across config drift.
        try
        {
            var payload = DecodePayload(token);
            if (payload is null) return [];

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (!root.TryGetProperty("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", out var roleProp)
                && !root.TryGetProperty("role", out roleProp)
                && !root.TryGetProperty("roles", out roleProp))
                return [];

            if (roleProp.ValueKind == JsonValueKind.String)
                return [roleProp.GetString()!];

            if (roleProp.ValueKind == JsonValueKind.Array)
                return roleProp.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse roles from token");
        }
        return [];
    }

    private int? ParseUserId(string token) =>
        int.TryParse(ParseClaim(token, "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", "nameid"),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    // Accepts the short JWT form as a fallback when the long SOAP URI isn't
    // present - same rationale as ParseRoles.
    private string? ParseClaim(string token, string claimType, string? shortForm = null)
    {
        try
        {
            var payload = DecodePayload(token);
            if (payload is null) return null;

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty(claimType, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            if (shortForm is not null && root.TryGetProperty(shortForm, out prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse claim from token");
            return null;
        }
    }

    private bool IsTokenExpired(string token)
    {
        var expiry = GetTokenExpiry(token);
        return expiry is null || expiry.Value <= DateTimeOffset.UtcNow;
    }

    private DateTimeOffset? GetTokenExpiry(string token)
    {
        try
        {
            var payload = DecodePayload(token);
            if (payload is null) return null;

            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("exp", out var exp))
                return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read token expiration");
        }
        return null;
    }

    private static byte[]? DecodePayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        var payload = parts[1];
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }
        payload = payload.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(payload);
    }
}
