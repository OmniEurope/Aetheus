// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Configuration;

/// <summary>Enables HTTPS redirection only when the middleware can resolve a port through
/// HTTPS_PORT, ANCM_HTTPS_PORT or a server HTTPS address. HTTP-only containers behind
/// TLS-terminating proxies therefore avoid a redirect configuration with no destination.</summary>
public static class HttpsPortConfiguration
{
    public static bool IsKnown(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.IsNullOrWhiteSpace(configuration["HTTPS_PORT"])
            || !string.IsNullOrWhiteSpace(configuration["ANCM_HTTPS_PORT"])
            || !string.IsNullOrWhiteSpace(configuration["HTTPS_PORTS"]))
            return true;
        if (IsHttpsUrlList(configuration["URLS"]))
            return true;
        return configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Any(endpoint => IsHttpsUrlList(endpoint["Url"]));
    }

    private static bool IsHttpsUrlList(string? urls) =>
        !string.IsNullOrWhiteSpace(urls)
        && urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
