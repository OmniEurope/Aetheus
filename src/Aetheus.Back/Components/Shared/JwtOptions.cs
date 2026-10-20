// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Shared;

/// <summary>
/// Resolved JWT signing configuration. Populated once at startup from <c>Auth:JwtKey</c>;
/// services must inject this instead of re-reading the configuration so the literal
/// fallback cannot leak through any code path.
/// </summary>
public sealed class JwtOptions
{
    private readonly string _signingKey = string.Empty;

    public required string SigningKey
    {
        get => _signingKey;
        init
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            // HS256 requires at least 32 bytes (256 bits) of key material.
            if (System.Text.Encoding.UTF8.GetByteCount(value) < 32)
                throw new ArgumentException(
                    "Auth:JwtKey must be at least 32 UTF-8 bytes for HS256.", nameof(value));
            _signingKey = value;
        }
    }

    public string Issuer { get; init; } = "Aetheus";
    public string Audience { get; init; } = "Aetheus";
    public TimeSpan DefaultLifetime { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan RememberMeLifetime { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Custom claim type that carries the user's <c>SecurityStamp</c>. Tokens whose claim does not
/// match the current persisted stamp are rejected during validation, providing immediate revocation
/// on password / role changes and explicit logout.
/// </summary>
public static class AetheusClaimTypes
{
    public const string SecurityStamp = "aetheus:sst";

    /// <summary>
    /// Present (value "true") on a token whose owner must change their password before using the
    /// app. The Front gates all navigation onto the mandatory change-password screen while this
    /// claim is set; a fresh token minted after the change no longer carries it.
    /// </summary>
    public const string MustChangePassword = "aetheus:mcp";

    /// <summary> The opaque identifier the application declares to its own audience measurement for
    /// a signed-in visitor. A keyed hash of the account identifier: it says nothing of the account
    /// and cannot be turned back into it without the signing key. The measurement hashes it again
    /// and never stores it. </summary>
    public const string AnalyticsVisitor = "aetheus:avid";
}
