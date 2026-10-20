// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Components.Shared;

/// <summary>
/// Shared token primitives used by <see cref="AuthService"/> and <see cref="ServerEnrollmentService"/>.
/// Extracted to a single helper so the HMAC keying is identical across both - and so neither service
/// needs a <c>partial</c> split to share it.
/// </summary>
internal static class AuthTokenHelper
{
    public static string GenerateSecureToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string HashToken(string signingKey, string token)
    {
        var key = Encoding.UTF8.GetBytes(signingKey);
        var bytes = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }

    /// <summary> The opaque audience identifier of an account (<see
    /// cref="AetheusClaimTypes.AnalyticsVisitor"/>): 128 bits of a keyed hash of the account
    /// identifier, under its own label so it shares nothing with the other values derived from the
    /// signing key. URL-safe characters only. </summary>
    public static string AnalyticsVisitorId(string signingKey, string accountId)
    {
        var bytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes("aetheus:analytics-visitor:v1:" + accountId));
        return Convert.ToBase64String(bytes, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string PadOrTrim(string input, int targetLength)
    {
        input ??= string.Empty;
        return input.Length == targetLength ? input : input.PadRight(targetLength, '\0');
    }
}
