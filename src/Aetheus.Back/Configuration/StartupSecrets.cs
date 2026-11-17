// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;

namespace Aetheus.Back.Configuration;

/// <summary>Resolves the JWT signing key at startup and refuses to run outside Development with a
/// missing secret, with one of the publicly known defaults committed in earlier appsettings.json
/// revisions (F-01), with the committed development administrator password, or without the public API
/// URL the agent archives and clone URLs are built from (M5).</summary>
internal static class StartupSecrets
{
    private static readonly string[] KnownDefaultJwtKeys = ["aetheus-dev-key-minimum-32-bytes!!"];
    private static readonly string[] KnownDefaultEncryptionKeys = ["aetheus-encryption-key-32ch!"];

    /// <summary>The administrator password committed in appsettings.Development.json.</summary>
    private const string DevelopmentAdminPassword = "aetheus-dev-admin-pwd";

    internal const string PublicApiBaseUrlKey = "Aetheus:PublicApiBaseUrl";

    /// <summary>The configured Auth:JwtKey; in Development, a per-process random key when it is unset.
    /// Throws outside Development when a secret is missing or burned.</summary>
    public static string ResolveJwtSigningKey(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var jwtKey = configuration["Auth:JwtKey"];
        if (string.IsNullOrEmpty(jwtKey))
            jwtKey = CreateEphemeralDevelopmentKey(isDevelopment);
        if (!isDevelopment)
            RejectUnsafeSecrets(configuration, jwtKey);
        return jwtKey;
    }

    private static string CreateEphemeralDevelopmentKey(bool isDevelopment)
    {
        if (!isDevelopment)
            throw new InvalidOperationException("Auth:JwtKey must be configured in non-development environments.");
        // Tokens are invalidated on restart - set Auth:JwtKey for stable dev sessions.
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        // #60: warn so devs understand why "Remember me" stops working after each restart.
        Console.WriteLine(
            "[WARN] Auth:JwtKey is not set. Generated an ephemeral signing key for this process; " +
            "all issued JWTs (including Remember Me sessions) will be invalidated on restart. " +
            "Configure Auth:JwtKey in appsettings.Development.json or user-secrets to persist sessions.");
        return key;
    }

    private static void RejectUnsafeSecrets(IConfiguration configuration, string jwtKey)
    {
        if (IsKnownDefault(KnownDefaultJwtKeys, jwtKey))
            throw new InvalidOperationException(
                "Auth:JwtKey is set to a publicly known default value. Rotate the signing key before starting in non-development environments.");

        var encKey = configuration["Auth:EncryptionKey"];
        if (!string.IsNullOrEmpty(encKey) && IsKnownDefault(KnownDefaultEncryptionKeys, encKey))
            throw new InvalidOperationException(
                "Auth:EncryptionKey is set to a publicly known default value. Rotate the encryption key before starting in non-development environments.");

        if (string.IsNullOrEmpty(encKey))
            throw new InvalidOperationException(
                "Auth:EncryptionKey must be configured in non-development environments (set via the Auth__EncryptionKey environment variable).");

        if (string.IsNullOrEmpty(configuration["Auth:EncryptionSalt"]))
            throw new InvalidOperationException(
                "Auth:EncryptionSalt must be configured in non-development environments (set via the Auth__EncryptionSalt environment variable, base64, >= 16 bytes).");

        var adminPassword = configuration["Auth:AdminPassword"];
        if (string.Equals(adminPassword, "admin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Auth:AdminPassword must not be the default 'admin' value in non-development environments.");

        // The development password is public (it is committed). A public demo advertises its password on
        // purpose, so only that one may keep it.
        if (string.Equals(adminPassword, DevelopmentAdminPassword, StringComparison.OrdinalIgnoreCase)
            && !PublicDemoConfiguration.IsEnabled(configuration))
            throw new InvalidOperationException(
                "Auth:AdminPassword must not be the committed development password in non-development environments.");

        // The agent archive and the clone URLs carry this URL; without it they would fall back to the
        // Host header of an anonymous request (HostUrlExtensions), which the client controls.
        var publicApiBaseUrl = configuration[PublicApiBaseUrlKey];
        if (!Uri.TryCreate(publicApiBaseUrl, UriKind.Absolute, out var publicApiUri)
            || (publicApiUri.Scheme != Uri.UriSchemeHttps && publicApiUri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException(
                "Aetheus:PublicApiBaseUrl must be configured with an absolute http(s) URL in non-development environments (set via the Aetheus__PublicApiBaseUrl environment variable).");

        // R-1: outside Development the seeded administrator always goes through its first-step account setup
        // (the public demo opts out by its own, separate configuration).
        if (!configuration.GetValue(SeededAdministratorSetup.ConfigurationKey, true))
            throw new InvalidOperationException(
                "Auth:AdministratorSetupRequired=false is a development setting: the seeded administrator must set up its own account in non-development environments.");
    }

    private static bool IsKnownDefault(string[] knownDefaults, string value) =>
        Array.Exists(knownDefaults, known => string.Equals(known, value, StringComparison.Ordinal));
}
