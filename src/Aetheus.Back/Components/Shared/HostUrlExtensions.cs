// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Shared;

internal static class HostUrlExtensions
{
    public static string ResolvePublicApiUrl(IConfiguration config, HttpContext httpContext)
    {
        // Configured value wins - deployments should set Aetheus:PublicApiBaseUrl in the
        // backend appsettings to the canonical API URL (e.g. https://aetheus-api.example.com).
        var configured = config[Aetheus.Back.Configuration.StartupSecrets.PublicApiBaseUrlKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        // Development only: outside Development the host refuses to start without the configured value
        // (StartupSecrets), so the request host, which the client controls, is never used there.
        return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
    }
}
