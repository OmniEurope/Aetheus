// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Aetheus.WebAnalytics;

internal static class AnalyticsPrivacyPolicy
{
    internal const string OptOutCookieName = "aetheus_analytics_optout";

    private static readonly string[] BotMarkers =
    [
        "bot", "crawler", "spider", "headless", "lighthouse", "monitoring", "uptime"
    ];

    public static bool IsOptedOut(HttpContext context, AetheusWebAnalyticsOptions options)
    {
        if (context.Request.Headers["Sec-GPC"] == "1")
            return true;
        if (options.HonorDoNotTrack && context.Request.Headers["DNT"] == "1")
            return true;
        return context.Request.Cookies.TryGetValue(OptOutCookieName, out var value)
               && value == "1";
    }

    public static async ValueTask<bool> IsOptedOutAsync(
        HttpContext context,
        AetheusWebAnalyticsOptions options)
    {
        if (IsOptedOut(context, options))
            return true;
        return context.User.Identity?.IsAuthenticated == true
               && options.AuthenticatedOptOutResolver is not null
               && await options.AuthenticatedOptOutResolver(context).ConfigureAwait(false);
    }

    public static bool IsExcludedRequest(HttpContext context, AetheusWebAnalyticsOptions options)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (BotMarkers.Any(marker => userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return true;

        var address = context.Connection.RemoteIpAddress;
        return address is not null
               && options.ExcludedIpNetworks.Any(network => IpNetworkMatcher.Contains(network, address));
    }

    public static string NormalizeRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route)
            || route.Length > 256
            || route[0] != '/'
            || route.Contains('?', StringComparison.Ordinal)
            || route.Contains('#', StringComparison.Ordinal)
            || route.Contains('@', StringComparison.Ordinal)
            || route.Contains('\\', StringComparison.Ordinal)
            || route.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("The analytics route is not an allowed rooted path.");

        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = segments.Select(segment =>
            Guid.TryParse(segment, out _) || long.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? "{id}"
                : segment.ToLowerInvariant());
        return "/" + string.Join('/', normalized);
    }
}
