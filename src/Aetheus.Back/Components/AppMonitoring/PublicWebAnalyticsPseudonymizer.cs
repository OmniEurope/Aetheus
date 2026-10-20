// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

internal static class PublicWebAnalyticsPseudonymizer
{
    public static AppWebAnalyticsIngestEvent Create(
        MonitoredApp app,
        string secret,
        PublicWebAnalyticsEventRequest source,
        string address)
    {
        var master = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var authenticated = !string.IsNullOrWhiteSpace(source.AuthenticatedUserId);
        var place = "network-prefix:" + NetworkPrefix(address);
        var identity = authenticated
            ? $"account:{source.AuthenticatedUserId}"
            : place;
        var instant = source.OccurredAtUtc;
        return new AppWebAnalyticsIngestEvent
        {
            SchemaVersion = 1,
            ApplicationId = app.Id,
            SiteId = app.AnalyticsSiteId ?? string.Empty,
            EventId = source.EventId,
            OccurredAtUtc = instant,
            Kind = source.Kind,
            Route = NormalizeRoute(source.Route),
            DurationMs = source.DurationMs,
            ErrorType = source.ErrorType,
            DailyPseudonym = Period(master, app.Id, "day", $"{instant:yyyy-MM-dd}", identity),
            WeeklyPseudonym = Period(
                master,
                app.Id,
                "week",
                $"{ISOWeek.GetYear(instant):0000}-W{ISOWeek.GetWeekOfYear(instant):00}",
                identity),
            MonthlyPseudonym = Period(master, app.Id, "month", $"{instant:yyyy-MM}", identity),
            // Recette R1-68: one person signed in at two places is two sessions of Current visitors, so
            // the session of an account is also keyed on the network it comes from. The visitor and
            // authenticated pseudonyms stay per account: the person is still counted once.
            SessionPseudonym = authenticated
                ? Stable(master, app.Id, app.AnalyticsPseudonymKeyVersion, "session", $"{identity}|{place}")
                : Period(master, app.Id, "session-day", $"{instant:yyyy-MM-dd}", identity),
            AuthenticatedPseudonym = authenticated
                ? Stable(master, app.Id, app.AnalyticsPseudonymKeyVersion, "authenticated", identity)
                : null,
            KeyVersion = app.AnalyticsPseudonymKeyVersion
        };
    }

    private static string Stable(
        byte[] master,
        int appId,
        int keyVersion,
        string purpose,
        string identity)
    {
        var key = HMACSHA256.HashData(
            master,
            Encoding.UTF8.GetBytes(
                $"aetheus:webanalytics:v1:app:{appId}:generation:{keyVersion}:{purpose}"));
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private static string Period(
        byte[] master,
        int appId,
        string kind,
        string period,
        string identity)
    {
        var key = HMACSHA256.HashData(
            master,
            Encoding.UTF8.GetBytes($"aetheus:webanalytics:v1:app:{appId}:{kind}:{period}"));
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private static string NetworkPrefix(string value)
    {
        var address = IPAddress.TryParse(value, out var parsed) ? parsed : IPAddress.None;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            bytes[3] = 0;
        else
            Array.Clear(bytes, 7, bytes.Length - 7);
        return Convert.ToHexStringLower(bytes);
    }

    private static string NormalizeRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route)
            || route.Length > 256
            || !route.StartsWith("/", StringComparison.Ordinal)
            || route.Contains('?', StringComparison.Ordinal)
            || route.Contains('#', StringComparison.Ordinal)
            || route.Contains('@', StringComparison.Ordinal))
            throw new InvalidOperationException("The analytics route is not allowed.");
        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Guid.TryParse(segment, out _)
                               || long.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? "{id}"
                : segment.ToLowerInvariant());
        return "/" + string.Join('/', segments);
    }
}
