// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Components.Auth;

internal static class DeploymentBootstrapIdentity
{
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromMinutes(15);

    internal static DateTimeOffset? GetActiveExpiration(IConfiguration config, TimeProvider timeProvider)
    {
        if (!DateTimeOffset.TryParse(
                config["Auth:DeploymentBootstrapExpiresAtUtc"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiresAt))
            return null;

        var now = timeProvider.GetUtcNow();
        return expiresAt > now && expiresAt <= now.Add(MaximumWindow) ? expiresAt : null;
    }

    internal static bool CredentialsMatch(LoginRequest request, string email, string password)
    {
        // Bitwise AND keeps both fixed-time comparisons observable on every attempt. A bootstrap identity
        // signs in like everyone else: by an e-mail address, compared the way stored e-mails are (trimmed,
        // lower-case).
        var expectedEmail = email.Trim().ToLowerInvariant();
        var suppliedEmail = AuthTokenHelper.PadOrTrim(
            request.Email.Trim().ToLowerInvariant(), expectedEmail.Length);
        var suppliedPassword = AuthTokenHelper.PadOrTrim(request.Password, password.Length);
        var usernameMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(suppliedEmail), Encoding.UTF8.GetBytes(expectedEmail));
        var passwordMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(suppliedPassword), Encoding.UTF8.GetBytes(password));
        return usernameMatches & passwordMatches;
    }
}
