// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Short-lived, project-scoped credential used only for an autonomous AI task checkout.
/// Unlike pipeline clone credentials it is keyed by the durable AI definition because no
/// pipeline run exists. The ten-minute expiry bounds exposure without persisting a secret.
/// </summary>
internal static class GitAiCloneToken
{
    internal const string UsernamePrefix = "aetheus-ai-";
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    internal static (string Username, string Password) Mint(
        IConfiguration configuration,
        int definitionId,
        int projectId,
        DateTime nowUtc)
    {
        var expiry = new DateTimeOffset(
            nowUtc.Add(DefaultTtl),
            TimeSpan.Zero).ToUnixTimeSeconds();
        var mac = Compute(configuration, definitionId, projectId, expiry);
        return (
            $"{UsernamePrefix}{definitionId.ToString(CultureInfo.InvariantCulture)}",
            $"{expiry.ToString(CultureInfo.InvariantCulture)}.{Base64Url(mac)}");
    }

    internal static int? Validate(
        IConfiguration configuration,
        string username,
        string password,
        int projectId,
        DateTime nowUtc)
    {
        if (!username.StartsWith(UsernamePrefix, StringComparison.Ordinal)
            || !int.TryParse(
                username.AsSpan(UsernamePrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var definitionId))
            return null;
        var separator = password.IndexOf('.');
        if (separator <= 0
            || !long.TryParse(
                password.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expiry)
            || DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime <= nowUtc)
            return null;

        byte[] presented;
        try
        {
            presented = Base64UrlDecode(password[(separator + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
        // Accepted under either configured secret, as the run clone tokens are, so a blue-green
        // switch to a dedicated RunTokenKey does not refuse what the other colour minted.
        foreach (var secret in GitCloneTokenSecrets.Validation(configuration))
        {
            if (CryptographicOperations.FixedTimeEquals(presented, Compute(secret, definitionId, projectId, expiry)))
                return definitionId;
        }
        return null;
    }

    private static byte[] Compute(
        IConfiguration configuration,
        int definitionId,
        int projectId,
        long expiry) =>
        Compute(GitCloneTokenSecrets.Signing(configuration, "autonomous AI Git clone tokens"), definitionId, projectId, expiry);

    private static byte[] Compute(
        string secret,
        int definitionId,
        int projectId,
        long expiry)
    {
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(secret),
            outputLength: 32,
            salt: null,
            info: Encoding.UTF8.GetBytes("git-ai-token:v1:hmac-key"));
        var message =
            $"git-ai-token:v1:{definitionId.ToString(CultureInfo.InvariantCulture)}:" +
            $"{projectId.ToString(CultureInfo.InvariantCulture)}:" +
            expiry.ToString(CultureInfo.InvariantCulture);
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized.PadRight(
            normalized.Length + (4 - normalized.Length % 4) % 4,
            '='));
    }
}
