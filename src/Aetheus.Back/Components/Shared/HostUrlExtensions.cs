// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Shared;

internal static class HostUrlExtensions
{
    public static string ResolvePublicApiUrl(IConfiguration config, HttpContext httpContext)
    {
        // Configured value wins - deployments should set Aetheus:PublicApiBaseUrl in the
        // backend appsettings to the canonical API URL (e.g. https://aetheus-api.example.com).
        var configured = config["Aetheus:PublicApiBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        // Dev fallback only: use the incoming request host.
        return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
    }
}
