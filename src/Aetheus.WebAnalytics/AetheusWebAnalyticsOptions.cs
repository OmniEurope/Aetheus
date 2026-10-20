// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Aetheus.WebAnalytics;

public sealed class AetheusWebAnalyticsOptions
{
    public bool Enabled { get; set; }
    public int ApplicationId { get; set; }
    public string SiteId { get; set; } = string.Empty;
    public Uri? IngestEndpoint { get; set; }
    public string IngestKey { get; set; } = string.Empty;
    public string PseudonymizationKey { get; set; } = string.Empty;
    public int PseudonymizationKeyVersion { get; set; } = 1;
    public bool ProductionOnly { get; set; } = true;
    public bool HonorDoNotTrack { get; set; } = true;
    public bool EnablePrivacyPage { get; set; }
    public int SessionTimeoutMinutes { get; set; } = 30;
    public int DetailedRetentionDays { get; set; } = 30;
    public int SessionRetentionDays { get; set; } = 90;
    public int AggregateRetentionMonths { get; set; } = 25;
    public int ExportIntervalMilliseconds { get; set; } = 3000;
    public string DisplayTimeZone { get; set; } = "UTC";
    public string ControllerName { get; set; } = string.Empty;
    public string Contact { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string LegalBasis { get; set; } = string.Empty;
    public string HostingDescription { get; set; } = string.Empty;
    public string PrivacyNoticeVersion { get; set; } = "2026-07-23";
    public IReadOnlyList<string> ExcludedIpNetworks { get; set; } = [];
    /// <summary>Allows a browser-supplied opaque visitor identifier when the host cannot
    /// authenticate SPA visitors. Disabled by default; host authentication takes
    /// precedence.</summary>
    public bool AcceptDeclaredUserId { get; set; }

    public Func<HttpContext, string?> AuthenticatedUserIdResolver { get; set; } = context =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    public Func<HttpContext, ValueTask<bool>>? AuthenticatedOptOutResolver { get; set; }
    public Func<HttpContext, bool, ValueTask>? AuthenticatedOptOutWriter { get; set; }

    internal void Validate()
    {
        if (!Enabled)
            return;
        ValidateIdentityAndIngest();
        ValidateRetention();
        ValidatePrivacyPage();
        if (ExcludedIpNetworks.Any(network => !IpNetworkMatcher.IsValid(network)))
            throw new InvalidOperationException(
                "ExcludedIpNetworks entries must be exact IP addresses or valid IPv4/IPv6 CIDR networks.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(DisplayTimeZone);
    }

    private void ValidateIdentityAndIngest()
    {
        if (ApplicationId <= 0)
            throw new InvalidOperationException("Aetheus Web Analytics requires a positive application id.");
        if (!SiteIdPolicy.IsValid(SiteId))
            throw new InvalidOperationException("SiteId must contain 1 to 64 lowercase letters, digits, dots, or hyphens.");
        if (IngestEndpoint is null || IngestEndpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Aetheus Web Analytics requires an HTTPS ingestion endpoint.");
        if (string.IsNullOrWhiteSpace(IngestKey))
            throw new InvalidOperationException("Aetheus Web Analytics requires an ingestion key.");
        if (PseudonymizationKey.Length < 32)
            throw new InvalidOperationException("PseudonymizationKey must contain at least 32 characters.");
        if (PseudonymizationKeyVersion <= 0)
            throw new InvalidOperationException("PseudonymizationKeyVersion must be positive.");
    }

    private void ValidateRetention()
    {
        if (SessionTimeoutMinutes is < 5 or > 120)
            throw new InvalidOperationException("SessionTimeoutMinutes must be between 5 and 120.");
        if (DetailedRetentionDays is < 7 or > 90)
            throw new InvalidOperationException("DetailedRetentionDays must be between 7 and 90.");
        if (SessionRetentionDays is < 30 or > 180)
            throw new InvalidOperationException("SessionRetentionDays must be between 30 and 180.");
        if (AggregateRetentionMonths is < 13 or > 37)
            throw new InvalidOperationException("AggregateRetentionMonths must be between 13 and 37.");
        if (ExportIntervalMilliseconds is < 100 or > 10_000)
            throw new InvalidOperationException("ExportIntervalMilliseconds must be between 100 and 10000.");
    }

    private void ValidatePrivacyPage()
    {
        if (EnablePrivacyPage && (string.IsNullOrWhiteSpace(ControllerName)
                                  || string.IsNullOrWhiteSpace(Contact)
                                  || string.IsNullOrWhiteSpace(Purpose)
                                  || string.IsNullOrWhiteSpace(LegalBasis)
                                  || string.IsNullOrWhiteSpace(HostingDescription)
                                   || string.IsNullOrWhiteSpace(PrivacyNoticeVersion)))
            throw new InvalidOperationException("The privacy page requires controller, contact, purpose, legal basis, and hosting information.");
    }
}
