// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The self-expiring identity a deployment hands to the candidate colour so its authenticated smoke
/// exercises real JWT, RBAC and SignalR paths without touching production data or a persisted account.
///
/// A shell cutover generated it in the process that then started the containers and ran the probe, so
/// it never existed anywhere else. Split into typed steps, the colour is started by one task and
/// probed by another, and the only channel between two tasks - a run variable published with
/// <c>setvariable</c> - is echoed into the run log and readable by anyone with Project.Read.
///
/// So it is not transported: it is <b>derived</b>. Both steps compute the same value from the run id
/// and a server-side key, and the control plane registers it for masking, so it never travels, is
/// never stored, and is redacted if it ever surfaces. It stays run-scoped and short-lived; no standing
/// account is created on the deployed environment.
/// </summary>
internal sealed record DeploymentBootstrapIdentity(
    string User,
    string Password,
    string Stamp,
    string ExpiresAtUtc)
{
    /// <summary>
    /// How long the deployed colour accepts this identity. The product caps the exceptional bootstrap
    /// window at 15 minutes; this asks for less, so the resulting JWT cannot outlive the deployment.
    /// </summary>
    internal const int LifetimeMinutes = 10;

    /// <summary>
    /// Whether this backend runs in production. Read from configuration rather than injected, because
    /// the derivation is a static helper on the task-building path; ASP.NET puts the environment name
    /// there under both spellings depending on how the host was started.
    /// </summary>
    private static bool IsProduction(IConfiguration configuration)
    {
        var environmentName = configuration["ASPNETCORE_ENVIRONMENT"]
                              ?? configuration["DOTNET_ENVIRONMENT"]
                              ?? configuration["Environment"];
        return string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Derives the identity for one run. Deterministic in the run id, so the step that starts the
    /// colour and the step that probes it agree without exchanging anything.
    /// </summary>
    internal static DeploymentBootstrapIdentity For(IConfiguration configuration, int runId, DateTime nowUtc)
    {
        // Prefer a dedicated key; fall back to the AES secret only so single-key deployments keep
        // working. Either way the HMAC key is HKDF-derived under a fixed label, so it is
        // domain-separated from every other use of the same raw secret.
        // Fail closed: an empty-keyed HMAC would make this identity predictable to anyone who knows
        // the run id, which on a production deployment is public. A missing secret is a hard
        // misconfiguration, never a silent downgrade.
        var dedicatedKey = configuration["Deployment:BootstrapIdentityKey"];

        // A360-59: in PRODUCTION the fallback is refused. HKDF separates the derivation domains, but it
        // cannot separate the consequences: while this identity is derived from Auth:EncryptionKey, a
        // leak of the data-encryption key is also a leak of every deployment's administrator credential.
        // Outside production the fallback stays, so a developer or a test environment is not forced to
        // configure a second secret to run a deployment.
        if (string.IsNullOrEmpty(dedicatedKey) && IsProduction(configuration))
        {
            throw new InvalidOperationException(
                "Deployment:BootstrapIdentityKey is required in production. The fallback on "
                + "Auth:EncryptionKey is refused here: sharing the data key would make one compromise "
                + "into two, turning a data-key leak into an administrator credential for every "
                + "environment this backend deploys.");
        }

        var secret = dedicatedKey ?? configuration["Auth:EncryptionKey"];
        if (string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException(
                "No signing key configured for the deployment bootstrap identity "
                + "(set Deployment:BootstrapIdentityKey or Auth:EncryptionKey).");
        }

        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(secret),
            outputLength: 32,
            salt: null,
            info: Encoding.UTF8.GetBytes("deployment-bootstrap:v1:hmac-key"));

        return new DeploymentBootstrapIdentity(
            // A recognisable prefix so an operator reading container environment sees what this is. The
            // identity signs in like everyone else, by an e-mail address; ".invalid" (RFC 2606) is reserved
            // so the address can never belong to a real mailbox or to a stored account.
            "deploy-smoke-" + Derive(key, runId, "user")[..16] + "@bootstrap.invalid",
            Derive(key, runId, "password"),
            Derive(key, runId, "stamp"),
            nowUtc.AddMinutes(LifetimeMinutes).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
    }

    private static string Derive(byte[] key, int runId, string purpose) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            key,
            Encoding.UTF8.GetBytes(
                $"deployment-bootstrap:v1:{purpose}:{runId.ToString(CultureInfo.InvariantCulture)}")));
}
