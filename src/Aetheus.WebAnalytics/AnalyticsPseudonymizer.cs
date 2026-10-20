// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Aetheus.WebAnalytics;

internal sealed class AnalyticsPseudonymizer(AetheusWebAnalyticsOptions options)
{
    private readonly byte[] _masterKey = SHA256.HashData(Encoding.UTF8.GetBytes(options.PseudonymizationKey));

    public AnalyticsExportEvent Create(HttpContext context, AnalyticsBrowserEvent source, string normalizedRoute)
    {
        var instant = source.OccurredAtUtc.UtcDateTime;
        var identity = ResolveIdentity(context, source, out var authenticated);
        var place = "network-prefix:" + NetworkPrefix(context.Connection.RemoteIpAddress ?? IPAddress.None);
        return new AnalyticsExportEvent
        {
            ApplicationId = options.ApplicationId,
            SiteId = options.SiteId,
            EventId = source.EventId,
            OccurredAtUtc = instant,
            Kind = source.Kind,
            Route = normalizedRoute,
            DurationMs = source.DurationMs,
            ErrorType = source.ErrorType,
            DailyPseudonym = DerivePeriodPseudonym("day", $"{instant:yyyy-MM-dd}", identity),
            WeeklyPseudonym = DerivePeriodPseudonym(
                "week",
                $"{ISOWeek.GetYear(instant):0000}-W{ISOWeek.GetWeekOfYear(instant):00}",
                identity),
            MonthlyPseudonym = DerivePeriodPseudonym("month", $"{instant:yyyy-MM}", identity),
            // Recette R1-68: one person signed in at two places is two sessions of Current visitors, so
            // the session of an account is also keyed on the network it comes from. The visitor and
            // authenticated pseudonyms stay per account: the person is still counted once.
            SessionPseudonym = authenticated
                ? DeriveStablePseudonym("session", $"{identity}|{place}")
                : DerivePeriodPseudonym("session-day", $"{instant:yyyy-MM-dd}", identity),
            AuthenticatedPseudonym = authenticated
                ? DeriveStablePseudonym("authenticated", identity)
                // A declared sign-in without an identifier carries no account, so it stays keyed on the network prefix and
                // on the month: a stable pseudonym of a network would follow it across months, which
                // the anonymous path never does. Counts are monthly, so nothing is lost.
                : source.SignedIn == true
                    ? DerivePeriodPseudonym("signed-in-month", $"{instant:yyyy-MM}", identity)
                    : null,
            KeyVersion = options.PseudonymizationKeyVersion
        };
    }

    private string ResolveIdentity(HttpContext context, AnalyticsBrowserEvent source, out bool authenticated)
    {
        var userId = context.User.Identity?.IsAuthenticated == true
            ? options.AuthenticatedUserIdResolver(context)
            : null;
        // A host that never sees the session takes the opaque identifier the application declares.
        // The host's own authentication always wins when it has one.
        if (string.IsNullOrWhiteSpace(userId) && options.AcceptDeclaredUserId)
            userId = source.AuthenticatedUserId;
        authenticated = !string.IsNullOrWhiteSpace(userId);
        if (authenticated)
            return $"account:{userId}";

        return "network-prefix:" + NetworkPrefix(context.Connection.RemoteIpAddress ?? IPAddress.None);
    }

    private static string NetworkPrefix(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            bytes[3] = 0;
        else
            Array.Clear(bytes, 7, bytes.Length - 7);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string DerivePeriodPseudonym(string periodKind, string period, string identity)
    {
        var periodKey = HMACSHA256.HashData(
            _masterKey,
            Encoding.UTF8.GetBytes(
                $"aetheus:webanalytics:v1:app:{options.ApplicationId}:{periodKind}:{period}"));
        return Convert.ToHexString(HMACSHA256.HashData(periodKey, Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private string DeriveStablePseudonym(string purpose, string identity)
    {
        var applicationKey = HMACSHA256.HashData(
            _masterKey,
            Encoding.UTF8.GetBytes(
                $"aetheus:webanalytics:v1:app:{options.ApplicationId}:generation:{options.PseudonymizationKeyVersion}:{purpose}"));
        return Convert.ToHexString(HMACSHA256.HashData(applicationKey, Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }
}
