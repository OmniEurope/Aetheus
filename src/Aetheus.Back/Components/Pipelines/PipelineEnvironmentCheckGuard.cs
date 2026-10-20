// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Decides whether a stage's environment lets the run in (P-22). Every required check on the
/// environment must pass; an absent environment or an environment with no checks lets the run through.
/// </summary>
public interface IPipelineEnvironmentCheckGuard
{
    /// <param name="projectId">The run's project (BACK-018): the stage's environment is read in it, so
    /// a homonym of another project never answers for it.</param>
    /// <returns><c>true</c> when the stage may proceed.</returns>
    Task<bool> CheckEnvironmentChecksAsync(PipelineStageDefinition stageDef, int? projectId, CancellationToken ct);
}

/// <summary>
/// The environment-check gate, extracted from <see cref="PipelineRunService"/>: it reads the
/// environment's checks and evaluates them, and it is where the SSRF guard on REST callbacks lives.
///
/// It touches nothing else in the run - no run state, no dispatch, no stage advancement - which is why
/// it comes out whole rather than as a set of helpers on the engine.
/// </summary>
public sealed class PipelineEnvironmentCheckGuard(
    IPipelineEnvironmentReader repo,
    IHttpClientFactory httpClientFactory,
    ILogger<PipelineEnvironmentCheckGuard> logger,
    // The DNS lookup of a REST callback host; the system resolver unless a test supplies one.
    Func<string, CancellationToken, Task<System.Net.IPAddress[]>>? resolve = null) : IPipelineEnvironmentCheckGuard
{
    public async Task<bool> CheckEnvironmentChecksAsync(PipelineStageDefinition stageDef, int? projectId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(stageDef.Environment)) return true;

        var env = await repo.FindEnvironmentByNameAsync(stageDef.Environment, projectId, ct).ConfigureAwait(false);
        if (env is null) return true;

        var checks = await repo.GetEnvironmentChecksAsync(env.Id, ct).ConfigureAwait(false);
        if (checks.Count == 0) return true;

        foreach (var check in checks.Where(c => c.IsRequired))
        {
            var passed = check.Type switch
            {
                EnvironmentCheckType.RestCallback => await EvaluateRestCheckAsync(check, ct).ConfigureAwait(false),
                _ => false
            };

            if (!passed)
            {
                logger.LogWarning("Environment check '{CheckName}' failed for environment '{EnvName}'", check.Name, env.Name);
                return false;
            }
        }

        return true;
    }

    private async Task<bool> EvaluateRestCheckAsync(EnvironmentCheck check, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(check.Configuration)) return false;

        try
        {
            var config = JsonSerializer.Deserialize<Dictionary<string, string>>(check.Configuration);
            if (config is null || !config.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url)) return false;

            // F-17: SSRF guard - only http/https, no redirects (handled by HttpClient policy),
            // and reject loopback / RFC1918 / link-local hosts so callbacks can't probe internal services.
            if (!IsSafeOutboundUrl(url))
            {
                logger.LogWarning("REST callback check '{CheckName}' rejected unsafe URL '{Url}'.", check.Name, url);
                return false;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(check.TimeoutSeconds));

            // F-17 (hardening): the literal check above only sees the host string. Resolve a DNS host and
            // reject if ANY resolved address is internal, closing the "DNS name that resolves to an internal
            // / cloud-metadata IP (e.g. 169.254.169.254)" hole. A narrow TOCTOU window remains if the name
            // re-resolves between here and connect; these checks are intended for trusted hosts only.
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && !System.Net.IPAddress.TryParse(parsed.Host, out _))
            {
                var resolved = await (resolve ?? System.Net.Dns.GetHostAddressesAsync)(parsed.Host, timeoutCts.Token).ConfigureAwait(false);
                if (resolved.Length == 0 || Array.Exists(resolved, IsBlockedIp))
                {
                    logger.LogWarning("REST callback check '{CheckName}' rejected URL '{Url}' - host resolves to an internal address.", check.Name, url);
                    return false;
                }
            }

            var httpClient = httpClientFactory.CreateClient(PipelinesModuleExtensions.EnvironmentCheckHttpClient);
            using var response = await httpClient.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or System.Net.Sockets.SocketException)
        {
            logger.LogWarning(ex, "REST callback check '{CheckName}' failed", check.Name);
            return false;
        }
    }

    private static bool IsSafeOutboundUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        // Reject by host name pattern first (covers DNS-only entries before resolution).
        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;

        if (System.Net.IPAddress.TryParse(host, out var ip) && IsBlockedIp(ip)) return false;
        return true;
    }

    // Delegates internal-address classification to the shared guard so SSRF blocklists cannot
    // diverge.
    private static bool IsBlockedIp(System.Net.IPAddress ip) =>
        Shared.WebhookSsrfGuard.IsForbiddenAddress(ip);
}
