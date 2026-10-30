// SPDX-License-Identifier: EUPL-1.2
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace Aetheus.Back.Components.Auth;

public class AuthService(
    IAuthRepository repo,
    IConfiguration config,
    IAuditService audit,
    JwtOptions jwtOptions,
    IMemoryCache cache,
    Aetheus.Back.Components.Organizations.IOrganizationService orgService,
    ITotpService totpService,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor,
    IAdminChangeNotifier adminNotifier,
    IDomainEventDispatcher domainEvents,
    ILogger<AuthService> logger) : IAuthService
{
    private const int MaxFailedAttempts = 5; // F-011
    private static readonly TimeSpan BaseLockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30); // F-012
    private static readonly TimeSpan RefreshTokenGrace = RefreshTokenRetention.RevokedRetention;
    public async Task<bool> IsRegistrationTokenValidAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var regToken = await repo.FindValidRegistrationTokenAsync(HashToken(token), ct).ConfigureAwait(false);
        return regToken is not null;
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        User? user = null;
        try
        {
            // Inactive users are deliberately returned here so their rejected attempts remain
            // distinguishable in the append-only audit trail. Every other auth path still checks
            // IsActive before issuing access.
            // Owner decision 2026-10-28: people sign in by e-mail address only. What is typed is looked up
            // as an e-mail and nowhere else, so a username typed in the field matches no account and is
            // refused exactly like a wrong password (no account enumeration).
            user = await repo.FindUserByEmailWithRolesAsync(request.Email, ct).ConfigureAwait(false);
            if (user is not null)
            {
                if (!user.IsActive)
                {
                    await LoginAuditRecorder.RecordFailureAsync(audit, httpContextAccessor, "Inactive", user, AttemptedLogin(request), ct).ConfigureAwait(false);
                    return null;
                }

                return await LoginDatabaseUserAsync(user, request, ct).ConfigureAwait(false);
            }

            var bootstrap = await LoginBootstrapAsync(request, ct).ConfigureAwait(false);
            if (bootstrap is null)
                await LoginAuditRecorder.RecordFailureAsync(audit, httpContextAccessor, "InvalidCredentials", null, AttemptedLogin(request), ct).ConfigureAwait(false);
            return bootstrap;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Interactive login failed unexpectedly for {Username}", LoginAuditRecorder.Sanitize(AttemptedLogin(request), 100));
            await LoginAuditRecorder.RecordFailureAsync(audit, httpContextAccessor, "Error", user, AttemptedLogin(request), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<LoginResponse?> LoginDatabaseUserAsync(
        User user,
        LoginRequest request,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (user.LockoutEndUtc is not null && user.LockoutEndUtc > now)
        {
            await LoginAuditRecorder.RecordFailureAsync(audit, httpContextAccessor, "LockedOut", user, AttemptedLogin(request), ct).ConfigureAwait(false);
            return null;
        }
        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            await HandleFailedLoginAsync(user, ct).ConfigureAwait(false);
            await LoginAuditRecorder.RecordFailureAsync(audit, httpContextAccessor, "InvalidCredentials", user, AttemptedLogin(request), ct).ConfigureAwait(false);
            return null;
        }
        var (secondFactorValid, challenge) = await ValidateSecondFactorAsync(user, request, ct).ConfigureAwait(false);
        if (!secondFactorValid) return challenge;
        if (user.FailedLoginCount > 0 || user.LockoutEndUtc is not null)
            await repo.ResetFailedLoginAsync(user.Id, ct).ConfigureAwait(false);
        await DetectLoginAnomalyAsync(user.Username, user.Id, ct).ConfigureAwait(false);
        // The administrator bootstrap (owner decision 2026-10-28) may make this account site Admin now: the
        // token issued below then already carries the role.
        var accessProved = new AccountAccessProvedEvent(user.Id, user.Username, user.Email);
        await domainEvents.DispatchAsync(accessProved, ct).ConfigureAwait(false);
        var roles = user.UserRoles.Select(item => item.Role.Name).ToList();
        if (accessProved.GrantedSiteAdmin && !roles.Contains(RoleNames.Admin, StringComparer.Ordinal))
            roles.Add(RoleNames.Admin);
        var claims = BuildUserClaims(user, roles);
        var response = await GenerateTokenResponseWithRefreshAsync(
            claims, user.Id, request.RememberMe, ct).ConfigureAwait(false);
        return response with { MustChangePassword = user.MustChangePassword };
    }

    private async Task<(bool IsValid, LoginResponse? Challenge)> ValidateSecondFactorAsync(
        User user,
        LoginRequest request,
        CancellationToken ct)
    {
        if (!user.TotpEnabled) return (true, null);
        if (string.IsNullOrEmpty(request.TotpCode) && string.IsNullOrEmpty(request.RecoveryCode))
            return (false, new LoginResponse { TotpRequired = true });
        var accepted = !string.IsNullOrEmpty(request.TotpCode)
            ? totpService.ValidateTotpCode(user.TotpSecret!, request.TotpCode)
            : await totpService.ConsumeRecoveryCodeAsync(user.Id, request.RecoveryCode!, ct).ConfigureAwait(false);
        if (accepted) return (true, null);
        await HandleFailedLoginAsync(user, ct).ConfigureAwait(false);
        await LoginAuditRecorder.RecordFailureAsync(audit, httpContextAccessor, "InvalidSecondFactor", user, AttemptedLogin(request), ct).ConfigureAwait(false);
        return (false, null);
    }

    private async Task<LoginResponse?> LoginBootstrapAsync(LoginRequest request, CancellationToken ct)
    {
        var hasDbUsers = await repo.AnyUsersExistAsync(ct).ConfigureAwait(false);
        var match = BootstrapCredentialPolicy.Match(request, config, timeProvider, hasDbUsers);
        return match is null ? null : BuildBootstrapResponse(request, match.ExpiresAt);
    }


    private LoginResponse BuildBootstrapResponse(LoginRequest request, DateTimeOffset? expiresAt)
    {
        // Bootstrap admin does not support TOTP or refresh tokens - short-lived JWT only.
        var bootstrapStamp = config["Auth:BootstrapStamp"] ?? "bootstrap";
        return GenerateTokenResponse([
            new Claim(ClaimTypes.Name, AttemptedLogin(request)),
            new Claim(ClaimTypes.NameIdentifier, "bootstrap"),
            new Claim(AetheusClaimTypes.SecurityStamp, bootstrapStamp),
            new Claim(ClaimTypes.Role, "Admin")
        ], request.RememberMe, expiresAt?.UtcDateTime);
    }

    // --- Lockout helpers (F-011) ---

    private async Task HandleFailedLoginAsync(User user, CancellationToken ct)
    {
        // The stored count, not the snapshot + 1: concurrent failures read the same snapshot.
        var newCount = await repo.IncrementFailedLoginAsync(user.Id, ct).ConfigureAwait(false);
        if (newCount >= MaxFailedAttempts)
        {
            // Exponential backoff: 15 min × 2^(lockout_round - 1), capped at 24 h
            var lockoutRound = newCount / MaxFailedAttempts;
            var duration = TimeSpan.FromTicks(
                Math.Min(BaseLockoutDuration.Ticks * (1L << Math.Min(lockoutRound - 1, 6)),
                          TimeSpan.FromHours(24).Ticks));
            var lockoutEnd = timeProvider.GetUtcNow().UtcDateTime.Add(duration);
            await repo.LockoutUserAsync(user.Id, lockoutEnd, ct).ConfigureAwait(false);
        }
    }

    // --- Login anomaly detection (F-013) ---

    private async Task DetectLoginAnomalyAsync(string username, int userId, CancellationToken ct)
    {
        var (clientIp, userAgent) = LoginAuditRecorder.GetClientContext(httpContextAccessor);

        // Check if this IP was seen in recent logins for this user. Null-safe: login must never
        // break because anomaly logging couldn't read history (the pattern handles a null result).
        var recentIps = await audit.GetRecentLoginIpsAsync(username, 50, ct).ConfigureAwait(false);
        var isNewIp = recentIps is { Count: > 0 } && !recentIps.Contains(clientIp);

        if (isNewIp)
        {
            logger.LogWarning("Login from new IP {Ip} for user {Username} (user-agent: {UserAgent})", clientIp, username, userAgent);
            await audit.LogAsync("LoginAnomaly", "User", userId, $"New IP: {clientIp}, UA: {userAgent}", ct).ConfigureAwait(false);
        }

        // Always log the successful login with IP/UA for future anomaly checks
        await audit.LogAsync("Login", "User", userId, $"IP: {clientIp}, UA: {userAgent}", ct).ConfigureAwait(false);
    }

    public async Task<bool> UnlockUserAsync(int userId, CancellationToken ct = default)
    {
        var user = await repo.FindUserByIdWithRolesAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return false;
        await repo.ClearLockoutAsync(userId, ct).ConfigureAwait(false);
        await audit.LogAsync("UnlockedUser", "User", userId, user.Username, ct).ConfigureAwait(false);
        return true;
    }

    private List<Claim> BuildUserClaims(User user, IEnumerable<string> roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(AetheusClaimTypes.SecurityStamp, user.SecurityStamp),
            new(AetheusClaimTypes.AnalyticsVisitor, AuthTokenHelper.AnalyticsVisitorId(jwtOptions.SigningKey, user.Id.ToString()))
        };
        // Carries the forced-change state through token renewal/refresh so the Front gate survives
        // a page reload; a token minted after the password is changed no longer includes it.
        if (user.MustChangePassword)
            claims.Add(new Claim(AetheusClaimTypes.MustChangePassword, "true"));
        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));
        return claims;
    }

    private LoginResponse GenerateTokenResponse(
        List<Claim> claims,
        bool rememberMe = false,
        DateTime? notAfterUtc = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var lifetime = rememberMe ? jwtOptions.RememberMeLifetime : jwtOptions.DefaultLifetime;
        var expiration = timeProvider.GetUtcNow().UtcDateTime.Add(lifetime);
        if (notAfterUtc is not null && notAfterUtc.Value < expiration)
            expiration = notAfterUtc.Value;

        var token = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            expires: expiration,
            signingCredentials: creds);

        return new LoginResponse
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAt = token.ValidTo
        };
    }

    /// <summary>
    /// Generates a single-use registration token used by an agent to enroll with the server.
    /// </summary>
    /// <remarks>
    /// <para><b>Plaintext-once contract (#56):</b> the returned <see cref="RegistrationTokenDto.Token"/>
    /// contains the plaintext value and is the ONLY chance the caller has to retrieve it.
    /// The database persists only a SHA-256 hash (mirroring <see cref="ServerToken.TokenHash"/>),
    /// so subsequent reads via <c>GetRegistrationTokensAsync</c> never expose plaintext again.
    /// Callers must surface the token to the operator immediately and never log it.</para>
    /// </remarks>
    public async Task<RegistrationTokenDto> CreateRegistrationTokenAsync(CreateRegistrationTokenRequest request, CancellationToken ct = default)
    {
        // Hardening (High #6): the entity's `Token` column now stores the SHA-256 hash of the
        // generated secret, mirroring the ServerToken.TokenHash treatment. Plaintext is
        // returned once in the response and never persisted.
        var plaintext = GenerateSecureToken();
        var orgId = request.OrganizationId
            ?? await orgService.GetDefaultOrganizationIdAsync(ct).ConfigureAwait(false)
            ?? throw new BadRequestException("No organization available; create an organization first.");
        var token = new RegistrationToken
        {
            Token = HashToken(plaintext),
            ExpiresAt = timeProvider.GetUtcNow().UtcDateTime.AddHours(request.ExpirationHours > 0 ? request.ExpirationHours : 24),
            OrganizationId = orgId
        };
        repo.AddRegistrationToken(token);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "RegistrationToken", token.Id, null, ct).ConfigureAwait(false);
        // S-FEAT-RT2W: push the new token to any open admin token list in realtime.
        await adminNotifier.BroadcastAsync(AdminEntities.RegistrationToken, token.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);

        return new RegistrationTokenDto
        {
            Id = token.Id,
            Token = plaintext,
            IsUsed = false,
            OrganizationId = token.OrganizationId,
            CreatedAt = token.CreatedAt,
            ExpiresAt = token.ExpiresAt
        };
    }

    public async Task<List<RegistrationTokenDto>> GetRegistrationTokensAsync(CancellationToken ct = default)
    {
        var tokens = await repo.GetRegistrationTokensAsync(ct).ConfigureAwait(false);
        return tokens.Select(MapRegistrationToken).ToList();
    }

    // RTOK: single-token lookup so the agent-wizard verify step can poll just *its* token's
    // IsUsed flag instead of re-downloading the full unpaginated list every 3 s.
    public async Task<RegistrationTokenDto?> GetRegistrationTokenAsync(int id, CancellationToken ct = default)
    {
        var token = await repo.FindRegistrationTokenByIdAsync(id, ct).ConfigureAwait(false);
        return token is null ? null : MapRegistrationToken(token);
    }

    // F-39: never echo the full plaintext registration token in admin reads; only the creator
    // received the secret once. Show a short prefix so admins can correlate logs.
    private static RegistrationTokenDto MapRegistrationToken(RegistrationToken t) => new()
    {
        Id = t.Id,
        Token = TruncateToken(t.Token),
        IsUsed = t.IsUsed,
        UsedByServerId = t.UsedByServerId,
        OrganizationId = t.OrganizationId,
        CreatedAt = t.CreatedAt,
        ExpiresAt = t.ExpiresAt
    };

    private static string TruncateToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return string.Empty;
        return token.Length <= 8 ? "***" : $"{token[..8]}…";
    }

    public async Task<int?> ValidateServerTokenAsync(string token, CancellationToken ct = default)
    {
        var hash = HashToken(token);
        return await repo.ValidateServerTokenHashAsync(hash, ct).ConfigureAwait(false);
    }

    public async Task<ServerRegistrationResponse?> RotateAgentTokenAsync(int serverId, CancellationToken ct = default)
    {
        if (!await repo.ServerExistsAsync(serverId, ct).ConfigureAwait(false))
            return null;

        await repo.RevokeServerTokensAsync(serverId, ct).ConfigureAwait(false);

        var bearerToken = GenerateSecureToken();
        var tokenHash = HashToken(bearerToken);
        var ttlDays = config.GetValue("AgentToken:TtlDays", 365);

        repo.AddServerToken(new ServerToken
        {
            ServerId = serverId,
            TokenHash = tokenHash,
            ExpiresAt = timeProvider.GetUtcNow().UtcDateTime.AddDays(ttlDays)
        });

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ServerRegistrationResponse
        {
            ServerId = serverId,
            BearerToken = bearerToken
        };
    }

    public async Task<ServerHeartbeatResponseDto?> MaybeRenewAgentTokenAsync(int serverId, CancellationToken ct = default)
    {
        var latestExpiry = await repo.GetLatestActiveServerTokenExpiryAsync(serverId, ct).ConfigureAwait(false);
        if (latestExpiry is null) return null; // no active token (cannot happen on an authenticated heartbeat)

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var renewWithinDays = config.GetValue("AgentToken:RenewWithinDays", 30);
        // Anti-churn: a fresh token already exists (e.g. agent hasn't swapped to
        // it yet, or another heartbeat just renewed) - don't mint another.
        if (latestExpiry.Value - now > TimeSpan.FromDays(renewWithinDays)) return null;

        var ttlDays = config.GetValue("AgentToken:TtlDays", 365);
        var bearerToken = GenerateSecureToken();
        var expiresAt = now.AddDays(ttlDays);

        // Add alongside the current token - the old one stays valid until its own
        // ExpiresAt, so a failed agent-side persist cannot lock the agent out.
        repo.AddServerToken(new ServerToken
        {
            ServerId = serverId,
            TokenHash = HashToken(bearerToken),
            ExpiresAt = expiresAt
        });
        await repo.DeleteExpiredServerTokensAsync(serverId, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ServerHeartbeatResponseDto
        {
            RenewedToken = bearerToken,
            RenewedTokenExpiresAtUtc = expiresAt
        };
    }

    public async Task<LoginResponse?> HandleExternalLoginAsync(string provider, string subjectId, string? displayName, string? email, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        // Disabled unless explicitly enabled with a provider allow-list + vetted IdP gateway.
        var enabled = config.GetValue("Auth:ExternalLogin:Enabled", false);
        if (!enabled) return null;

        var allowed = config.GetSection("Auth:ExternalLogin:AllowedProviders")
            .Get<string[]>() ?? [];
        if (allowed.Length == 0 ||
            !allowed.Contains(provider, StringComparer.OrdinalIgnoreCase))
            return null;

        var externalLogin = await repo.FindExternalLoginAsync(provider, subjectId, ct).ConfigureAwait(false);

        User user;
        if (externalLogin is not null)
        {
            user = externalLogin.User;
        }
        else
        {
            var autoProvision = config.GetValue("Auth:ExternalLogin:AutoProvision", false);
            if (!autoProvision) return null;

            // Canonical username "{provider}_{subjectId}" - never reuse a local account from displayName.
            var canonicalUsername = $"{provider}_{subjectId}";
            var existing = await repo.FindUserWithRolesAsync(canonicalUsername, ct).ConfigureAwait(false);
            if (existing is not null)
                return null;

            // PLAN-003 P3-2: every account signs in by a unique e-mail. A provider that gives none, or
            // one another account already holds, provisions nothing: linking would hand that account over.
            var normalizedEmail = Aetheus.Back.Data.UserEmail.Normalize(email);
            if (normalizedEmail is null || await repo.EmailInUseAsync(normalizedEmail, null, ct).ConfigureAwait(false))
            {
                logger.LogWarning("External login of {Provider} not provisioned: e-mail missing or already used", provider);
                return null;
            }

            user = new User
            {
                Username = canonicalUsername,
                PasswordHash = string.Empty,
                Email = normalizedEmail
            };

            await repo.AddUserAsync(user, ct).ConfigureAwait(false);

            await repo.AddExternalLoginAsync(new ExternalLogin
            {
                UserId = user.Id,
                Provider = provider,
                ProviderSubjectId = subjectId,
                DisplayName = displayName
            }, ct).ConfigureAwait(false);

            user = (await repo.FindUserWithRolesAsync(user.Username, ct).ConfigureAwait(false))!;
            await audit.LogAsync("ExternalLoginCreated", "User", user.Id, user.Username, ct).ConfigureAwait(false);
        }

        if (!user.IsActive) return null;

        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var claims = BuildUserClaims(user, roles);
        claims.Add(new Claim("provider", provider));

        return GenerateTokenResponse(claims);
    }

    public async Task<bool> IsSecurityStampValidAsync(int userId, string stamp, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(stamp)) return false;

        // Hardening / perf (#26): cache the user's current stamp briefly so each authenticated
        // request doesn't issue a DB roundtrip. Cache is invalidated explicitly on stamp bumps
        // (logout, password change, role change) - bounded TTL also bounds staleness.
        var cacheKey = $"sec-stamp:{userId}";
        if (!cache.TryGetValue<string>(cacheKey, out var current))
        {
            current = await repo.GetSecurityStampAsync(userId, ct).ConfigureAwait(false);
            if (current is not null)
                cache.Set(cacheKey, current, TimeSpan.FromSeconds(30));
        }
        return current is not null && string.Equals(current, stamp, StringComparison.Ordinal);
    }

    public async Task BumpSecurityStampAsync(int userId, CancellationToken ct = default)
    {
        await repo.BumpSecurityStampAsync(userId, ct).ConfigureAwait(false);
        cache.Remove($"sec-stamp:{userId}");
    }

    public async Task<LoginResponse?> RenewTokenAsync(int userId, CancellationToken ct = default)
    {
        var user = await repo.FindUserByIdWithRolesAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return null;

        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var claims = BuildUserClaims(user, roles);
        return await GenerateTokenResponseWithRefreshAsync(claims, userId, rememberMe: false, ct).ConfigureAwait(false);
    }

    public Task LogoutAsync(int userId, CancellationToken ct = default) =>
        domainEvents.DispatchAsync(new UserSessionEndedEvent(userId, UserSessionEndReasons.Logout), ct);

    // --- Refresh Token Rotation (F-012) ---

    public async Task<LoginResponse?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default) =>
        (await RefreshTokenWithReasonAsync(refreshToken, ct).ConfigureAwait(false)).Response;

    public async Task<RefreshTokenOutcome> RefreshTokenWithReasonAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = HashToken(refreshToken);
        var stored = await repo.FindRefreshTokenByHashAsync(tokenHash, ct).ConfigureAwait(false);
        if (stored is null) return RefreshTokenOutcome.Rejected(RefreshRejectionCodes.UnknownToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (stored.IsRevoked) return await ReuseRevokedTokenAsync(stored, stored, now, ct).ConfigureAwait(false);
        if (stored.IsExpired(now)) return RefreshTokenOutcome.Rejected(RefreshRejectionCodes.Expired);
        if (stored.User is null || !stored.User.IsActive) return RefreshTokenOutcome.Rejected(RefreshRejectionCodes.UserInactive);

        // Rotate: mint the replacement, then revoke the old token only if it is still active.
        var newPlaintext = GenerateSecureToken();
        var newRefresh = new Data.Entities.RefreshToken
        {
            UserId = stored.UserId,
            TokenHash = HashToken(newPlaintext),
            ExpiresAt = now.Add(RefreshTokenLifetime),
            CreatedAt = now
        };
        await repo.AddRefreshTokenAsync(newRefresh, ct).ConfigureAwait(false);
        await repo.TouchLastActivityAsync(stored.UserId, now, ct).ConfigureAwait(false);
        if (!await repo.RevokeRefreshTokenAsync(stored.Id, newRefresh.Id, ct).ConfigureAwait(false))
        {
            // A concurrent request rotated (or a logout revoked) this token after our read. Retire
            // the replacement nobody will receive, then answer from the token's fresh state exactly
            // as a request arriving just after that rotation would be answered: grace window or
            // replay.
            await repo.RevokeRefreshTokenAsync(newRefresh.Id, null, ct).ConfigureAwait(false);
            var current = await repo.FindRefreshTokenStateAsync(stored.Id, ct).ConfigureAwait(false);
            return current is null
                ? RefreshTokenOutcome.Rejected(RefreshRejectionCodes.UnknownToken)
                : await ReuseRevokedTokenAsync(stored, current, now, ct).ConfigureAwait(false);
        }

        // Prune dead tokens opportunistically
        await repo.DeleteExpiredRefreshTokensAsync(stored.UserId, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        var u = stored.User;
        var r = u.UserRoles.Select(ur => ur.Role.Name).ToList();
        var c = BuildUserClaims(u, r);
        var response = GenerateTokenResponse(c);
        return new RefreshTokenOutcome(response with { RefreshToken = newPlaintext }, null);
    }

    // Reuse of a revoked refresh token. Within the grace window, a token rotated into a replacement belongs
    // to a request that was already in flight: re-issue an access token only, never another refresh token.
    // Outside it, or when it was revoked without a replacement, the reuse reads as theft: revoke the chain.
    private async Task<RefreshTokenOutcome> ReuseRevokedTokenAsync(
        Data.Entities.RefreshToken stored, Data.Entities.RefreshToken state, DateTime now, CancellationToken ct)
    {
        if (state.RevokedAt is { } revokedAt && now - revokedAt <= RefreshTokenGrace && state.ReplacedById is { } replacementId
            && await IsStillActiveAsync(replacementId, now, ct).ConfigureAwait(false))
        {
            if (stored.User is not { IsActive: true } user) return RefreshTokenOutcome.Rejected(RefreshRejectionCodes.UserInactive);
            return new RefreshTokenOutcome(GenerateTokenResponse(BuildUserClaims(user, user.UserRoles.Select(ur => ur.Role.Name).ToList())), null);
        }
        await repo.RevokeAllUserRefreshTokensAsync(stored.UserId, ct).ConfigureAwait(false);
        // The whole chain is revoked as a possible theft; drop what is held in memory for the user
        // too.
        await domainEvents.DispatchAsync(
            new UserSessionEndedEvent(stored.UserId, UserSessionEndReasons.RefreshReplay), ct).ConfigureAwait(false);
        return RefreshTokenOutcome.Rejected(RefreshRejectionCodes.Replay);
    }

    // AUD-01 (recette R1-50): the grace window only covers a request in flight during a rotation. Once
    // the chain was ended (password change, logout, theft response: revoked with no replacement), the
    // rotated token must not mint an access token either. A replacement that was itself rotated onward
    // is still a live chain: another tab refreshing at the same moment must not read as a theft.
    private async Task<bool> IsStillActiveAsync(int tokenId, DateTime now, CancellationToken ct) =>
        await repo.FindRefreshTokenStateAsync(tokenId, ct).ConfigureAwait(false) is { } replacement
        && !replacement.IsExpired(now)
        && (replacement.RevokedAt is null || replacement.ReplacedById is not null);

    private async Task<LoginResponse> GenerateTokenResponseWithRefreshAsync(
        List<Claim> claims, int userId, bool rememberMe, CancellationToken ct)
    {
        var response = GenerateTokenResponse(claims, rememberMe);

        // Issue a refresh token for DB-backed users (not bootstrap admin)
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var plaintext = GenerateSecureToken();
        var refresh = new Data.Entities.RefreshToken
        {
            UserId = userId,
            TokenHash = HashToken(plaintext),
            ExpiresAt = now.Add(RefreshTokenLifetime),
            CreatedAt = now
        };
        await repo.AddRefreshTokenAsync(refresh, ct).ConfigureAwait(false);
        await repo.TouchLastActivityAsync(userId, now, ct).ConfigureAwait(false);

        return response with { RefreshToken = plaintext };
    }

    // TOTP methods delegated to ITotpService (injected as totpService)
    public Task<TotpSetupResponse> SetupTotpAsync(int userId, CancellationToken ct = default)
        => totpService.SetupTotpAsync(userId, ct);

    public Task<bool> VerifyAndEnableTotpAsync(int userId, string code, CancellationToken ct = default)
        => totpService.VerifyAndEnableTotpAsync(userId, code, ct);

    public Task<bool> DisableTotpAsync(int userId, string password, CancellationToken ct = default)
        => totpService.DisableTotpAsync(userId, password, ct);

    /// <summary>
    /// Git Smart-HTTP Basic-auth verification. Deliberately enforces lockout + BCrypt but NOT TOTP: the
    /// git CLI cannot present a second factor, so this path is exempt from MFA (same trade-off the
    /// external-login flow documents). The surface is limited to Git HTTP and is further gated by the
    /// run-scoped clone token / RBAC downstream. Accounts that must stay MFA-only should not be used for
    /// Git Basic-auth (use a dedicated access token instead).
    /// </summary>
    public async Task<string?> AuthenticateBasicAsync(string email, string password, CancellationToken ct = default)
    {
        // Same rule as LoginAsync: the sign-in is the e-mail address, matched case-insensitively.
        var user = await repo.FindUserByEmailWithRolesAsync(email, ct).ConfigureAwait(false);
        if (user is null || !user.IsActive) return null;

        if (user.LockoutEndUtc.HasValue && user.LockoutEndUtc.Value > timeProvider.GetUtcNow().UtcDateTime)
            return null;

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            await HandleFailedLoginAsync(user, ct).ConfigureAwait(false);
            return null;
        }

        // Align with LoginAsync: also clear a now-expired LockoutEndUtc, not just a non-zero fail count.
        if (user.FailedLoginCount > 0 || user.LockoutEndUtc is not null)
            await repo.ResetFailedLoginAsync(user.Id, ct).ConfigureAwait(false);

        return user.Username;
    }

    /// <summary>What the caller typed to sign in: the e-mail address.</summary>
    private static string AttemptedLogin(LoginRequest request) => request.Email;

    private static string GenerateSecureToken() => AuthTokenHelper.GenerateSecureToken();

    private string HashToken(string token) => AuthTokenHelper.HashToken(jwtOptions.SigningKey, token);

}
