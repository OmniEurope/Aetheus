// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Back.Components.PackageFeeds;

/// <summary>What the resolver and the version lister both read off a registry answer before its body.</summary>
internal static class RegistryResponses
{
    /// <summary>The origin of an http(s) address, or null when it is blank or not one.</summary>
    public static string? ResolveOrigin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return null;
        return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    /// <summary>
    /// The outcome already settled by the status code (rate limited, not found), or null for an answer whose
    /// body is to be read. Any other failure status throws <see cref="HttpRequestException"/>.
    /// </summary>
    public static (PackageResolveOutcome Outcome, TimeSpan? RetryAfter)? EarlyOutcome(HttpResponseMessage response, TimeProvider timeProvider)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return (PackageResolveOutcome.RateLimited, RetryAfter(response, timeProvider));
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (PackageResolveOutcome.NotFound, null);
        response.EnsureSuccessStatusCode();
        return null;
    }

    public static TimeSpan? RetryAfter(HttpResponseMessage response, TimeProvider timeProvider)
    {
        var retry = response.Headers.RetryAfter?.Delta;
        if (retry is null && response.Headers.RetryAfter?.Date is { } retryAt)
            retry = retryAt - timeProvider.GetUtcNow();
        return retry <= TimeSpan.Zero ? null : retry;
    }
}
