// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aetheus.Agent.Core.Services;

/// <summary>
/// The agent's TLS validation strategy and the startup guard that keeps it honest. It lives with the
/// services rather than with the DI registration so the backend probe can use it without reaching up
/// into the composition root.
/// </summary>
internal static class AgentTlsPosture
{
    // S-FEAT-24: choose the TLS validation strategy. Pinning (preferred for the dev cert) accepts the
    // server cert only when its SHA-256 thumbprint matches the configured pin - far narrower than the
    // accept-any AllowInsecureCerts escape hatch. Falls back to accept-any, then to default validation.
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation",
        Justification = "Reachable only with AllowInsecureCerts and no pinned thumbprint; ValidateTlsPosture refuses to start outside loopback in that state.")]
    [SuppressMessage("Aetheus.Security", "SEC008",
        Justification = "Same guard as CA5359 above: accept-any is reachable only with AllowInsecureCerts and no pin, and ValidateTlsPosture refuses that state outside loopback.")]
    internal static RemoteCertificateValidationCallback? BuildCertValidationCallback(AetheusAgentOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PinnedServerCertThumbprint))
        {
            var pinned = options.PinnedServerCertThumbprint.Replace(":", "", StringComparison.Ordinal).Trim();
            return (_, cert, _, _) =>
            {
                if (cert is null) return false;
                using var cert2 = new X509Certificate2(cert);
                var thumb = cert2.GetCertHashString(HashAlgorithmName.SHA256);
                return string.Equals(thumb, pinned, StringComparison.OrdinalIgnoreCase);
            };
        }

        // CA5359 is right that this accepts any certificate. It is reachable only when
        // AllowInsecureCerts is set AND no thumbprint is pinned, and ValidateTlsPosture below
        // refuses to start the agent in that state against anything but loopback or
        // host.docker.internal. The guard, not this callback, is what keeps it honest.
        return options.AllowInsecureCerts ? (_, _, _, _) => true : null;
    }

    // AllowInsecureCerts disables ALL TLS validation (agent channel, self-update download,
    // pipeline git clones). That is tolerable only against a local dev backend: loopback or
    // Docker Desktop's host alias (the VPS-sim enrolls via https://host.docker.internal).
    // Anywhere else, fail fast at startup instead of silently running a MITM-able agent;
    // self-signed remote backends must use PinnedServerCertThumbprint (which takes
    // precedence over AllowInsecureCerts) instead.
    internal static void ValidateTlsPosture(AetheusAgentOptions options)
    {
        if (!options.AllowInsecureCerts || !string.IsNullOrWhiteSpace(options.PinnedServerCertThumbprint))
            return;

        var host = new Uri(options.ServerUrl, UriKind.Absolute).Host;
        // Uri.Host returns the IPv6 literal WITH brackets ("[::1]"), so match both forms.
        var isLocalDev = host is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "host.docker.internal";
        if (!isLocalDev)
        {
            throw new InvalidOperationException(
                $"AllowInsecureCerts=true is only allowed against a local dev backend (loopback or host.docker.internal); ServerUrl host is '{host}'. " +
                "For a self-signed remote backend, configure Aetheus:PinnedServerCertThumbprint instead.");
        }
    }
}
