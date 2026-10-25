// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aetheus.WebAnalytics;

public static class AetheusWebAnalyticsServiceExtensions
{
    private const string EnabledEnvironmentVariable = "AETHEUS_WEB_ANALYTICS_ENABLED";

    public static IServiceCollection AddAetheusWebAnalytics(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AetheusWebAnalyticsOptions>? configure = null)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(AetheusWebAnalyticsMarker)))
            return services;

        var options = ReadOptions(configuration);
        configure?.Invoke(options);
        var enabledOverride = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);
        if (bool.TryParse(enabledOverride, out var enabled))
            options.Enabled = enabled;

        options.Validate();
        services.AddSingleton<AetheusWebAnalyticsMarker>();
        services.AddSingleton(options);
        if (!options.Enabled)
            return services;

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AnalyticsRequestRateLimiter>();
        services.AddSingleton<AnalyticsPseudonymizer>();
        services.AddSingleton<AnalyticsExportQueue>();
        services.AddHostedService<AnalyticsExportService>();
        services.AddHttpClient(AnalyticsExportService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(3));
        services.AddRazorPages().AddApplicationPart(typeof(AetheusWebAnalyticsServiceExtensions).Assembly);
        return services;
    }

    private static AetheusWebAnalyticsOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("Aetheus:WebAnalytics");
        return new AetheusWebAnalyticsOptions
        {
            Enabled = section.GetValue("Enabled", false),
            ApplicationId = ReadInt(
                section["ApplicationId"],
                Environment.GetEnvironmentVariable("AETHEUS_WEB_ANALYTICS_APPLICATION_ID")),
            SiteId = ReadString(section, "SiteId", "AETHEUS_WEB_ANALYTICS_SITE_ID"),
            IngestEndpoint = ReadUri(section, "IngestEndpoint", "AETHEUS_WEB_ANALYTICS_INGEST_ENDPOINT"),
            IngestKey = ReadString(section, "IngestKey", "AETHEUS_WEB_ANALYTICS_INGEST_KEY"),
            PseudonymizationKey = ReadString(
                section, "PseudonymizationKey", "AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY"),
            PseudonymizationKeyVersion = ReadInt(
                section["PseudonymizationKeyVersion"],
                Environment.GetEnvironmentVariable(
                    "AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY_VERSION"),
                1),
            ProductionOnly = section.GetValue("ProductionOnly", true),
            HonorDoNotTrack = section.GetValue("HonorDoNotTrack", true),
            EnablePrivacyPage = section.GetValue("EnablePrivacyPage", false),
            AcceptDeclaredUserId = section.GetValue("AcceptDeclaredUserId", false),
            SessionTimeoutMinutes = section.GetValue("SessionTimeoutMinutes", 30),
            DetailedRetentionDays = section.GetValue("DetailedRetentionDays", 30),
            SessionRetentionDays = section.GetValue("SessionRetentionDays", 90),
            AggregateRetentionMonths = section.GetValue("AggregateRetentionMonths", 25),
            ExportIntervalMilliseconds = section.GetValue("ExportIntervalMilliseconds", 3000),
            DisplayTimeZone = section["DisplayTimeZone"] ?? "UTC",
            ControllerName = section["ControllerName"] ?? string.Empty,
            Contact = section["Contact"] ?? string.Empty,
            Purpose = section["Purpose"] ?? string.Empty,
            LegalBasis = section["LegalBasis"] ?? string.Empty,
            HostingDescription = section["HostingDescription"] ?? string.Empty,
            PrivacyNoticeVersion = section["PrivacyNoticeVersion"] ?? "2026-07-23",
            ExcludedIpNetworks = section.GetSection("ExcludedIpNetworks").Get<string[]>()
                ?? section.GetSection("ExcludedIpPrefixes").Get<string[]>()
                ?? []
        };
    }

    private static string ReadString(IConfiguration section, string key, string environmentVariable) =>
        section[key] ?? Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty;

    private static Uri? ReadUri(IConfiguration section, string key, string environmentVariable) =>
        Uri.TryCreate(ReadString(section, key, environmentVariable), UriKind.Absolute, out var endpoint)
            ? endpoint
            : null;

    private static int ReadInt(string? configured, string? environmentValue, int fallback = 0) =>
        int.TryParse(
            configured ?? environmentValue,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    private sealed class AetheusWebAnalyticsMarker;
}
