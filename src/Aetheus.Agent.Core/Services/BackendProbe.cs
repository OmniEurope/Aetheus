// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using Aetheus.Agent.Core.Extensions;

namespace Aetheus.Agent.Core.Services;

/// <summary>
/// One-shot "can the agent actually reach the backend?" probe used by the
/// installer right after appsettings.json is written and right BEFORE the
/// systemd unit starts. This catches the runtime-specific failure modes that
/// the install script's <c>curl</c> check cannot:
/// <list type="bullet">
///   <item>TLS cert chain that .NET trusts but the system curl wouldn't (or vice versa)</item>
///   <item>HTTP/2 / ALPN negotiation differences</item>
///   <item>DNS resolution via the .NET resolver vs. libc</item>
///   <item>Proxy / HTTP_PROXY handling specific to <see cref="HttpClient"/></item>
/// </list>
/// Without this, those failures would surface only after <c>systemctl start</c>
/// timed out - operators would see a 2-minute hang followed by a cryptic systemd
/// error. With it, the install fails fast with the actual HTTP status.
/// </summary>
public static class BackendProbe
{
    private const int DefaultTimeoutSeconds = 10;

    /// <summary>
    /// Convenience overload used by <c>Program.cs</c> from the <c>--probe-backend</c>
    /// CLI mode. Calls <see cref="ProbeAsync"/>, prints the resulting message,
    /// and returns the process exit code (0 = reachable, 1 = not). Kept here so
    /// every agent host (Linux / Windows) wires it identically.
    /// </summary>
    public static async Task<int> RunAsync(string serverUrl, string[] args, bool allowInsecure = false, CancellationToken ct = default)
        => await RunWithTlsOptionsAsync(serverUrl, args, allowInsecure, pinnedThumbprint: null, ct).ConfigureAwait(false);

    internal static async Task<int> RunWithTlsOptionsAsync(
        string serverUrl,
        string[] args,
        bool allowInsecure,
        string? pinnedThumbprint,
        CancellationToken ct = default)
    {
        var timeout = ParseTimeout(args);
        using var handler = CreateHandler(serverUrl, allowInsecure, pinnedThumbprint);
        var result = await ProbeAsync(serverUrl, timeout, handler, ct).ConfigureAwait(false);
        Console.WriteLine(result.Message);
        return result.Ok ? 0 : 1;
    }

    /// <summary>The probe timeout: <c>--timeout N</c> when N is a positive number of seconds, capped
    /// at 60, otherwise the default.</summary>
    internal static TimeSpan ParseTimeout(string[] args)
    {
        var timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--timeout", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var seconds) && seconds > 0)
            {
                timeout = TimeSpan.FromSeconds(Math.Min(seconds, 60));
            }
        }
        return timeout;
    }

    // Use the exact runtime TLS callback precedence (pin, then local-development
    // AllowInsecureCerts, then platform validation) so installer success predicts whether the
    // subsequently started agent can connect. Null when platform validation applies unchanged.
    internal static SocketsHttpHandler? CreateHandler(string serverUrl, bool allowInsecure, string? pinnedThumbprint)
    {
        var options = new AetheusAgentOptions
        {
            ServerUrl = serverUrl,
            AllowInsecureCerts = allowInsecure,
            PinnedServerCertThumbprint = pinnedThumbprint
        };
        var certValidation = AgentCoreServiceCollectionExtensions.BuildCertValidationCallback(options);
        return certValidation is null
            ? null
            : new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = certValidation
                }
            };
    }

    /// <summary>
    /// Performs the actual HTTP call. Public so tests can drive it without a
    /// configured host. Always honours <paramref name="timeout"/> - the
    /// installer doesn't want a probe that takes 60 s to fail.
    /// </summary>
    public static Task<ProbeResult> ProbeAsync(string serverUrl, TimeSpan timeout, CancellationToken ct = default)
        => ProbeAsync(serverUrl, timeout, handler: null, ct);

    /// <summary>
    /// S-TECH-F2J7: I/O-decoupled core. Tests pass a fake <paramref name="handler"/> to drive the
    /// unreachable/timeout branches deterministically - no real network call (the previous test hit
    /// TEST-NET-1 and was flaky under parallel load). Production callers use the parameterless overload.
    /// </summary>
    [SuppressMessage("Aetheus.Security", "SEC006",
        Justification = "One-shot installer probe run once per process before the agent host exists; there is no DI container to take a factory from, and one client per process exhausts nothing.")]
    internal static async Task<ProbeResult> ProbeAsync(string serverUrl, TimeSpan timeout, HttpMessageHandler? handler, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return new ProbeResult(false, 0, "ServerUrl is empty in appsettings.json - re-run the installer.");
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri))
            return new ProbeResult(false, 0, $"ServerUrl '{serverUrl}' is not a valid absolute URL.");

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = timeout;
        client.BaseAddress = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/");
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync("health/live", ct).ConfigureAwait(false);
            sw.Stop();
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return new ProbeResult(true, status, $"Backend reachable in {sw.ElapsedMilliseconds} ms (HTTP {status}).");
            return new ProbeResult(false, status, $"Backend responded HTTP {status} on /health/live (expected 200). URL: {baseUri}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new ProbeResult(false, 0, $"Backend did not respond within {timeout.TotalSeconds:F0} s. URL: {baseUri}");
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return new ProbeResult(false, 0, $"Backend unreachable ({ex.Message}). URL: {baseUri}");
        }
    }

    public readonly record struct ProbeResult(bool Ok, int HttpStatus, string Message);
}
