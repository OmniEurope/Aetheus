// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Stateless, self-expiring credential a pipeline run uses to clone its project's internal Git
/// mirror over smart-HTTP (the <c>GITHUB_TOKEN</c> model). Minted at dispatch and injected as
/// <c>GIT_USERNAME</c>/<c>GIT_PASSWORD</c> into the (encrypted) clone-task env; validated by
/// <see cref="GitBasicAuthenticationHandler"/>.
///
/// The token is an HMAC-SHA256 over <c>runId:projectId:expiry</c> keyed by a server secret, so it is
/// unforgeable, scoped to exactly one project (it can only clone that repo), and carries its own
/// expiry - nothing is persisted, no revocation table, no migration. Any backend that shares the key
/// (a single deployment in prod; the shared dev secret across worktrees) can validate a token another
/// minted, so the clone works regardless of which backend served the run or owns the mirror on disk.
/// </summary>
internal static class GitRunCloneToken
{
    public const string UsernamePrefix = "aetheus-run-";

    /// <summary>Lifetime of a minted clone credential - generous enough for a slow checkout, short
    /// enough that a leaked token is useless soon after the run.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(6);

    public static (string Username, string Password) Mint(
        IConfiguration config, int runId, int projectId, DateTime nowUtc, TimeSpan ttl)
    {
        var expiryUnix = new DateTimeOffset(nowUtc.Add(ttl), TimeSpan.Zero).ToUnixTimeSeconds();
        var mac = Compute(config, runId, projectId, expiryUnix);
        var username = $"{UsernamePrefix}{runId.ToString(CultureInfo.InvariantCulture)}";
        var password = $"{expiryUnix.ToString(CultureInfo.InvariantCulture)}.{Base64Url(mac)}";
        return (username, password);
    }

    /// <summary>Returns the run id when <paramref name="username"/>/<paramref name="password"/> is a
    /// valid, unexpired clone token scoped to <paramref name="projectId"/>; otherwise <c>null</c>.</summary>
    public static int? Validate(
        IConfiguration config, string username, string password, int projectId, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(username) || !username.StartsWith(UsernamePrefix, StringComparison.Ordinal))
            return null;
        if (!int.TryParse(username.AsSpan(UsernamePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var runId))
            return null;

        var dot = password.IndexOf('.');
        if (dot <= 0)
            return null;
        if (!long.TryParse(password.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var expiryUnix))
            return null;
        if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix).UtcDateTime <= nowUtc)
            return null;

        byte[] presented;
        try
        {
            presented = Base64UrlDecode(password[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        // Recette R-464: a token is accepted under either configured secret. While a blue-green switch
        // moves from the Auth:EncryptionKey fallback to a dedicated RunTokenKey, the two colours sign
        // with different secrets; each must still accept what the other minted. Both are server
        // secrets, so this widens nothing an outsider can forge.
        foreach (var secret in GitCloneTokenSecrets.Validation(config))
        {
            if (CryptographicOperations.FixedTimeEquals(presented, Compute(secret, runId, projectId, expiryUnix)))
                return runId;
        }
        return null;
    }

    private static byte[] Compute(IConfiguration config, int runId, int projectId, long expiryUnix)
    {
        // Prefer a dedicated RunTokenKey; fall back to the AES secret only so dev/single-key
        // deployments keep working. Either way the HMAC key is HKDF-derived with a fixed info label,
        // so the signing key is domain-separated from any other use of the same raw secret (no
        // cross-domain key reuse even on the Auth:EncryptionKey fallback). A missing secret fails
        // closed (GitCloneTokenSecrets.Signing).
        return Compute(GitCloneTokenSecrets.Signing(config, "git run clone tokens"), runId, projectId, expiryUnix);
    }

    private static byte[] Compute(string secret, int runId, int projectId, long expiryUnix)
    {
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(secret),
            outputLength: 32,
            salt: null,
            info: Encoding.UTF8.GetBytes("git-run-token:v1:hmac-key"));
        var message = $"git-run-token:v1:{runId.ToString(CultureInfo.InvariantCulture)}:" +
                      $"{projectId.ToString(CultureInfo.InvariantCulture)}:{expiryUnix.ToString(CultureInfo.InvariantCulture)}";
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
