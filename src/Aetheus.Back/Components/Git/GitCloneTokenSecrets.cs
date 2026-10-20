// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git;

/// <summary>Signs clone tokens with GitLight:RunTokenKey, falling back to Auth:EncryptionKey. Treat
/// empty configuration values as absent because Compose can emit empty strings for unset
/// variables.</summary>
internal static class GitCloneTokenSecrets
{
    /// <summary>The secret new tokens are signed with.</summary>
    /// <exception cref="InvalidOperationException">No secret is configured: an empty-keyed HMAC would
    /// be trivially forgeable, so this fails closed.</exception>
    internal static string Signing(IConfiguration config, string tokenName)
    {
        var dedicated = config["GitLight:RunTokenKey"];
        var secret = string.IsNullOrEmpty(dedicated) ? config["Auth:EncryptionKey"] : dedicated;
        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException(
                $"No signing key configured for {tokenName} (set GitLight:RunTokenKey or Auth:EncryptionKey).");
        return secret;
    }

    /// <summary>
    /// Every secret a presented token is checked against. While a blue-green switch moves from the
    /// fallback to a dedicated key, the two colours sign with different secrets and each must accept
    /// what the other minted. Both are server secrets, so this widens nothing an outsider can forge.
    /// </summary>
    internal static IEnumerable<string> Validation(IConfiguration config)
    {
        var dedicated = config["GitLight:RunTokenKey"];
        var fallback = config["Auth:EncryptionKey"];
        if (!string.IsNullOrEmpty(dedicated)) yield return dedicated;
        if (!string.IsNullOrEmpty(fallback) && !string.Equals(fallback, dedicated, StringComparison.Ordinal))
            yield return fallback;
    }
}
