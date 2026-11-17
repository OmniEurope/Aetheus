// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PersonalAccessTokens;

internal sealed class PersonalAccessTokenService(
    IPersonalAccessTokenRepository repo,
    TokenHashKeys tokenHashKeys,
    TimeProvider timeProvider,
    IAuditService audit)
    : IPersonalAccessTokenService
{
    /// <summary>Only rewrite LastUsedAt when it is at least this stale, to keep the auth path cheap.</summary>
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(1);

    public async Task<CreatedPersonalAccessTokenDto> CreateAsync(int userId, CreatePersonalAccessTokenRequest request, CancellationToken ct = default)
    {
        if (request.ExpirationDays is < 1 or > 90)
            throw new ValidationException("Personal access tokens must expire within 1 to 90 days.");

        var plaintext = PatConstants.TokenPrefix + AuthTokenHelper.GenerateSecureToken();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var token = new PersonalAccessToken
        {
            UserId = userId,
            Name = request.Name.Trim(),
            TokenHash = tokenHashKeys.Hash(TokenHashPurpose.PersonalAccessToken, plaintext),
            TokenPrefix = plaintext[..Math.Min(PatConstants.PrefixDisplayLength, plaintext.Length)],
            Scope = request.Scope,
            ExpiresAt = now.AddDays(request.ExpirationDays)
        };

        repo.Add(token);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "PersonalAccessToken", token.Id, $"scope={request.Scope}", ct).ConfigureAwait(false);

        // CreatedAt is populated by AppDbContext.SaveChangesAsync on insert, so Map runs after the save.
        return new CreatedPersonalAccessTokenDto { Token = Map(token), PlaintextToken = plaintext };
    }

    public async Task<PaginatedResult<PersonalAccessTokenDto>> GetForUserAsync(
        int userId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (tokens, total) = await repo.GetForUserPagedAsync(
            userId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<PersonalAccessTokenDto>
        {
            Items = tokens.Select(Map).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<bool> RevokeAsync(int id, int userId, CancellationToken ct = default)
    {
        var token = await repo.FindByIdForUserAsync(id, userId, ct).ConfigureAwait(false);
        if (token is null || token.IsRevoked)
            return false;

        token.RevokedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Revoked", "PersonalAccessToken", id, null, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<PatPrincipal?> ValidateAsync(string plaintextToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken)
            || !plaintextToken.StartsWith(PatConstants.TokenPrefix, StringComparison.Ordinal))
            return null;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var token = await repo.FindActiveByHashWithUserAsync(
            tokenHashKeys.HashCandidates(TokenHashPurpose.PersonalAccessToken, plaintextToken), now, ct).ConfigureAwait(false);
        if (token is null)
            return null;

        // Usage trace, throttled so a busy token does not write on every request.
        if (token.LastUsedAt is null || now - token.LastUsedAt.Value >= LastUsedThrottle)
            await repo.TouchLastUsedAsync(token.Id, now, ct).ConfigureAwait(false);

        // Roles are the user's CURRENT roles (freshly loaded) - the PAT can never outrank them.
        var roles = token.User.UserRoles.Select(ur => ur.Role.Name).ToList();
        return new PatPrincipal(token.UserId, token.User.Username, roles, token.Scope, token.Id);
    }

    private static PersonalAccessTokenDto Map(PersonalAccessToken t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Scope = t.Scope,
        TokenPrefix = t.TokenPrefix,
        CreatedAt = t.CreatedAt,
        ExpiresAt = t.ExpiresAt,
        LastUsedAt = t.LastUsedAt,
        RevokedAt = t.RevokedAt
    };
}
