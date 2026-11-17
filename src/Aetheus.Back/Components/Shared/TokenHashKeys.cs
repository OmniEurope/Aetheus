// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Components.Shared;

/// <summary>What a keyed token hash or a keyed identifier is computed for (R-53). Each purpose has its own
/// key, so a value computed for one purpose is worthless for any other and none of them is the JWT signing
/// key itself.</summary>
public enum TokenHashPurpose
{
    RefreshToken,
    RegistrationToken,
    ServerToken,
    PasswordSetupToken,
    PersonalAccessToken,
    IngestKey,
    IngestKeyDerivation,
    AnalyticsVisitor,
    PackageRunToken
}

/// <summary>
/// R-53: the per-purpose HMAC keys used to hash stored tokens and to derive keyed identifiers. Each key is
/// derived with HKDF-SHA256 from the master secret (the configured JWT signing key material, so no new
/// configuration is needed) with a distinct <c>info</c> label per purpose. The signing key itself is no
/// longer used as an HMAC key for anything but the legacy fallback below.
/// </summary>
/// <remarks>Rotating the signing key rotates every derived key with it: every stored token hash and every
/// analytics visitor identifier then changes, exactly as it did before R-53.</remarks>
public sealed class TokenHashKeys
{
    private const int KeyLength = 32;

    private static readonly FrozenDictionary<TokenHashPurpose, string> Labels = new Dictionary<TokenHashPurpose, string>
    {
        [TokenHashPurpose.RefreshToken] = "aetheus/refresh-token-hash/v1",
        [TokenHashPurpose.RegistrationToken] = "aetheus/enrollment-token-hash/v1",
        [TokenHashPurpose.ServerToken] = "aetheus/agent-token-hash/v1",
        [TokenHashPurpose.PasswordSetupToken] = "aetheus/password-setup-token-hash/v1",
        [TokenHashPurpose.PersonalAccessToken] = "aetheus/personal-access-token-hash/v1",
        [TokenHashPurpose.IngestKey] = "aetheus/ingest-key-hash/v1",
        [TokenHashPurpose.IngestKeyDerivation] = "aetheus/ingest-key-derivation/v1",
        [TokenHashPurpose.AnalyticsVisitor] = "aetheus/analytics-visitor/v1",
        [TokenHashPurpose.PackageRunToken] = "aetheus/package-run-token/v1"
    }.ToFrozenDictionary();

    private readonly FrozenDictionary<TokenHashPurpose, byte[]> _keys;

    // R-53 legacy: the signing key used directly as the HMAC key, kept only to verify hashes stored before R-53.
    private readonly byte[] _legacyKey;

    public TokenHashKeys(JwtOptions jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(jwtOptions);
        _legacyKey = Encoding.UTF8.GetBytes(jwtOptions.SigningKey);
        _keys = Enum.GetValues<TokenHashPurpose>().ToFrozenDictionary(
            purpose => purpose,
            purpose => HKDF.DeriveKey(
                HashAlgorithmName.SHA256, _legacyKey, KeyLength, salt: null, info: Encoding.UTF8.GetBytes(Labels[purpose])));
    }

    /// <summary>The hash to store for a new token of that purpose, and the first one a lookup tries.</summary>
    public string Hash(TokenHashPurpose purpose, string token) => Hmac(_keys[purpose], token);

    /// <summary>
    /// The hashes a stored token of that purpose may carry: the per-purpose hash first, then the legacy one.
    /// </summary>
    /// <remarks>R-53 legacy fallback: the second entry is the pre-R-53 hash keyed directly with the signing
    /// key, so tokens issued before the change keep working. Remove it (return the first entry only) once the
    /// longest token lifetime has elapsed after the R-53 deployment: agent tokens (<c>AgentToken:TtlDays</c>,
    /// 365 days by default) are the longest, then refresh tokens (30 days) and personal access tokens
    /// (90 days at most).</remarks>
    public string[] HashCandidates(TokenHashPurpose purpose, string token) => [Hash(purpose, token), Hmac(_legacyKey, token)];

    /// <summary> The opaque audience identifier of an account (<see cref="AetheusClaimTypes.AnalyticsVisitor"/>):
    /// 128 bits of a keyed hash of the account identifier under the analytics key. URL-safe characters only.
    /// </summary>
    public string AnalyticsVisitorId(string accountId)
    {
        var bytes = HMACSHA256.HashData(_keys[TokenHashPurpose.AnalyticsVisitor], Encoding.UTF8.GetBytes(accountId));
        return Convert.ToBase64String(bytes, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The raw HMAC-SHA256 of <paramref name="message"/> under the key of <paramref name="purpose"/>: the
    /// signature of a stateless token such as a pipeline run's package registry credential.</summary>
    public byte[] Sign(TokenHashPurpose purpose, string message) =>
        HMACSHA256.HashData(_keys[purpose], Encoding.UTF8.GetBytes(message));

    /// <summary>The derived key of a purpose (tests prove the keys are distinct).</summary>
    internal byte[] KeyFor(TokenHashPurpose purpose) => (byte[])_keys[purpose].Clone();

    private static string Hmac(byte[] key, string token) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(token)));
}
