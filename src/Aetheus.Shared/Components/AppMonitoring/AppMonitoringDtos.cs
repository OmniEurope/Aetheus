// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary>Read model for a monitored application (ADR-021 phase 1: availability).</summary>
public sealed record MonitoredAppDto
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public int? EnvironmentId { get; init; }
    public string? EnvironmentName { get; init; }
    public int? ServerId { get; init; }
    public string? ServerName { get; init; }

    /// <summary> For an app without <see cref="ServerId"/> (probed by the backend), the fleet
    /// server whose Apache virtual host serves the probe URL's host; null when none or several do.
    /// Informative only, the probe still runs from the backend. </summary>
    public MonitoredAppHostingServerDto? HostingServer { get; init; }

    public string Name { get; init; } = string.Empty;
    public string? ProbeUrl { get; init; }
    public int ProbeIntervalSeconds { get; init; }
    public int ProbeTimeoutSeconds { get; init; }
    public int ExpectedStatusCode { get; init; }
    public int FailureThreshold { get; init; }
    public int RecoveryThreshold { get; init; }
    public bool Enabled { get; init; }
    public AppHealthStatus CurrentStatus { get; init; }
    public DateTime? LastStatusChangeAt { get; init; }
    public DateTime? LastCheckedAt { get; init; }
    public int? LastResponseTimeMs { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>Uptime ratio over the trailing 24 h, 7 d, 90 d in [0,1]; null when never sampled.</summary>
    public double? Uptime24h { get; init; }
    public double? Uptime7d { get; init; }
    public double? Uptime90d { get; init; }

    // OTLP ingestion (phase 2+): the key plaintext is never returned, only whether one is configured.
    public bool HasIngestKey { get; init; }
    public DateTime? IngestKeyCreatedAt { get; init; }
    public long IngestDroppedCount { get; init; }
    public DateTime? LastIngestAt { get; init; }
}

/// <summary>The fleet server deduced to host a backend-probed application.</summary>
public sealed record MonitoredAppHostingServerDto
{
    public int ServerId { get; init; }
    [StringLength(200)] public string ServerName { get; init; } = string.Empty;
}

/// <summary>Plaintext OTLP ingestion key, shown exactly once at generation (never persisted in clear).</summary>
public sealed record IngestKeyResponse
{
    public string Key { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

public sealed record MetricPointDto
{
    public DateTime Timestamp { get; init; }
    public double Value { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? P95 { get; init; }
}

public sealed record MetricSeriesDto
{
    public string MetricName { get; init; } = string.Empty;
    public string? Unit { get; init; }
    public IReadOnlyList<MetricPointDto> Points { get; init; } = [];
}

/// <summary>
/// Opaque daily visitor identifier produced server-side by the monitored application. It must be a
/// keyed digest; raw IP addresses and user-agent strings are never accepted by this contract.
/// </summary>
public sealed record AppVisitorIngestRequest
{
    [Required]
    [StringLength(64, MinimumLength = 64)]
    [RegularExpression("^[a-fA-F0-9]{64}$")]
    public string VisitorId { get; init; } = string.Empty;
}

public sealed record AppVisitorPointDto
{
    public DateOnly DayUtc { get; init; }
    public int UniqueVisitors { get; init; }
}

public sealed record AppVisitorSeriesDto
{
    public int Today { get; init; }
    public IReadOnlyList<AppVisitorPointDto> Points { get; init; } = [];
}

public abstract record WebAnalyticsEventRequest
{
    [Range(1, 1)]
    public int SchemaVersion { get; init; }

    [Required]
    public Guid EventId { get; init; }

    [Required]
    public DateTime OccurredAtUtc { get; init; }

    [Required]
    [StringLength(32, MinimumLength = 1)]
    public string Kind { get; init; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Route { get; init; } = string.Empty;

    [Range(0, 300_000)]
    public int? DurationMs { get; init; }

    [StringLength(32, MinimumLength = 1)]
    [RegularExpression("^[a-z0-9_]+$")]
    public string? ErrorType { get; init; }
}

public sealed record AppWebAnalyticsIngestEvent : WebAnalyticsEventRequest
{
    [Range(1, int.MaxValue)]
    public int ApplicationId { get; init; }

    [Required]
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression("^[a-z0-9.-]+$")]
    public string SiteId { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 64)]
    [RegularExpression("^[a-f0-9]{64}$")]
    public string DailyPseudonym { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 64)]
    [RegularExpression("^[a-f0-9]{64}$")]
    public string WeeklyPseudonym { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 64)]
    [RegularExpression("^[a-f0-9]{64}$")]
    public string MonthlyPseudonym { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 64)]
    [RegularExpression("^[a-f0-9]{64}$")]
    public string SessionPseudonym { get; init; } = string.Empty;

    [StringLength(64, MinimumLength = 64)]
    [RegularExpression("^[a-f0-9]{64}$")]
    public string? AuthenticatedPseudonym { get; init; }

    [Range(1, int.MaxValue)]
    public int KeyVersion { get; init; }
}

public sealed record AppWebAnalyticsPointDto
{
    public DateOnly DayUtc { get; init; }
    public int UniqueVisitors { get; init; }
    public int Sessions { get; init; }
    public int ReturningVisitors { get; init; }
    public long PageViews { get; init; }
}

public sealed record AppWebAnalyticsPageDto
{
    public string Route { get; init; } = string.Empty;
    public long PageViews { get; init; }
}

public sealed record AppWebAnalyticsSummaryDto
{
    public int UniqueVisitorsToday { get; init; }
    public int UniqueVisitorsThisWeek { get; init; }
    public int UniqueVisitorsThisMonth { get; init; }
    public int AuthenticatedUniqueThisMonth { get; init; }
    public int SessionsThisMonth { get; init; }
    public int ReturningVisitorsThisMonth { get; init; }
    public long PageViewsThisMonth { get; init; }
    public int BrowserPerformanceSamplesLast30Days { get; init; }
    public double? AverageBrowserNavigationDurationMs { get; init; }
    public int? P95BrowserNavigationDurationMs { get; init; }
    public int BrowserErrorsLast30Days { get; init; }
    public DateTime? LastIngestAtUtc { get; init; }
    public long RejectedEvents { get; init; }
    public long EstimatedStorageBytes { get; init; }
    public long StorageBudgetBytes { get; init; }
    public int StorageUsagePercent { get; init; }
    public IReadOnlyList<AppWebAnalyticsPointDto> Daily { get; init; } = [];
    public IReadOnlyList<AppWebAnalyticsPageDto> TopPages { get; init; } = [];
}

public sealed record ConfigureAppWebAnalyticsRequest
{
    public bool Enabled { get; init; }
    public bool PublicIngestEnabled { get; init; }

    [Required]
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression("^[a-z0-9.-]+$")]
    public string SiteId { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(10)]
    [MaxItemStringLength(2048)]
    public List<string> AllowedOrigins { get; init; } = [];

    [Range(1_048_576, 10_737_418_240)]
    public long StorageBudgetBytes { get; init; } = AppMonitoringDefaults.DefaultAnalyticsStorageBudgetBytes;
}

public sealed record AppWebAnalyticsConfigurationDto
{
    public bool Enabled { get; init; }
    public bool PublicIngestEnabled { get; init; }
    public string SiteId { get; init; } = string.Empty;
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
    public long StorageBudgetBytes { get; init; }
    public int PseudonymKeyVersion { get; init; }
    public DateTime? PseudonymKeyCreatedAt { get; init; }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(
    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PublicWebAnalyticsEventRequest
    : WebAnalyticsEventRequest
{
    /// <summary>
    /// Optional authenticated-user identifier supplied by the host application (an opaque id already
    /// known to it, never a raw PII value such as an email). Hashed server-side into
    /// <see cref="AppWebAnalyticsIngestEvent.AuthenticatedPseudonym"/> by <c>PublicWebAnalyticsPseudonymizer</c>
    /// and never persisted in raw form.
    /// </summary>
    [StringLength(256, MinimumLength = 1)]
    public string? AuthenticatedUserId { get; init; }
}

public sealed record AppMetricThresholdDto
{
    public int Id { get; init; }
    public int MonitoredAppId { get; init; }
    public string MetricName { get; init; } = string.Empty;
    public ComparisonOperator Operator { get; init; }
    public double Threshold { get; init; }
    public bool Enabled { get; init; }
    public bool IsBreached { get; init; }
    public DateTime? LastTriggeredAt { get; init; }
}

public sealed record CreateMetricThresholdRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string MetricName { get; set; } = string.Empty;

    [Required]
    public ComparisonOperator Operator { get; set; }

    public double Threshold { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed record AppLogEntryDto
{
    /// <summary>The stored record's id: tells grid rows apart when a live refresh marks the new ones.</summary>
    public int Id { get; init; }
    public DateTime Timestamp { get; init; }
    public int SeverityNumber { get; init; }
    public string? SeverityText { get; init; }
    public string Body { get; init; } = string.Empty;
    public string? AttributesJson { get; init; }
}

public sealed record AppErrorEventDto
{
    public int Id { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public string ExceptionType { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? TopFrame { get; init; }
    public int OccurrenceCount { get; init; }
    public DateTime FirstSeenAt { get; init; }
    public DateTime LastSeenAt { get; init; }
}

public abstract record MonitoredAppRequest
{
    [Required]
    [StringLength(150, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    public int? EnvironmentId { get; set; }

    /// <summary>Server hosting the app; null = app is off-fleet and probed directly by the backend (public URL only).</summary>
    public int? ServerId { get; set; }

    [StringLength(2048)]
    [Url]
    public string? ProbeUrl { get; set; }

    [Range(AppMonitoringDefaults.MinimumProbeIntervalSeconds, AppMonitoringDefaults.MaximumProbeIntervalSeconds)]
    public int ProbeIntervalSeconds { get; set; } = AppMonitoringDefaults.DefaultProbeIntervalSeconds;

    [Range(AppMonitoringDefaults.MinimumProbeTimeoutSeconds, AppMonitoringDefaults.MaximumProbeTimeoutSeconds)]
    public int ProbeTimeoutSeconds { get; set; } = AppMonitoringDefaults.DefaultProbeTimeoutSeconds;

    [Range(AppMonitoringDefaults.MinimumExpectedStatusCode, AppMonitoringDefaults.MaximumExpectedStatusCode)]
    public int ExpectedStatusCode { get; set; } = AppMonitoringDefaults.DefaultExpectedStatusCode;

    [Range(AppMonitoringDefaults.MinimumTransitionThreshold, AppMonitoringDefaults.MaximumTransitionThreshold)]
    public int FailureThreshold { get; set; } = AppMonitoringDefaults.DefaultFailureThreshold;

    [Range(AppMonitoringDefaults.MinimumTransitionThreshold, AppMonitoringDefaults.MaximumTransitionThreshold)]
    public int RecoveryThreshold { get; set; } = AppMonitoringDefaults.DefaultRecoveryThreshold;

    public bool Enabled { get; set; } = true;
}

public sealed record CreateMonitoredAppRequest : MonitoredAppRequest
{
    [Required]
    public int ProjectId { get; set; }
}

public sealed record UpdateMonitoredAppRequest : MonitoredAppRequest;

/// <summary>A single availability data point (raw sample).</summary>
public sealed record AppHealthSampleDto
{
    public DateTime Timestamp { get; init; }
    public bool IsUp { get; init; }
    public int? ResponseTimeMs { get; init; }
    public int? StatusCode { get; init; }
    public string? Error { get; init; }
}

/// <summary>Probe configuration handed to an agent for the apps it hosts (pull model).</summary>
public sealed record AppProbeConfigDto
{
    public int MonitoredAppId { get; init; }
    public string ProbeUrl { get; init; } = string.Empty;
    public int ProbeIntervalSeconds { get; init; }
    public int ProbeTimeoutSeconds { get; init; }
    public int ExpectedStatusCode { get; init; }
}

/// <summary>A probe result reported by an agent (or produced by the backend prober).</summary>
public sealed record AppProbeResultDto
{
    [Required]
    public int MonitoredAppId { get; init; }

    public DateTime Timestamp { get; init; }

    public bool IsUp { get; init; }

    [Range(0, int.MaxValue)]
    public int? ResponseTimeMs { get; init; }

    [Range(0, 599)]
    public int? StatusCode { get; init; }

    [StringLength(500)]
    public string? Error { get; init; }

    public static AppProbeResultDto Failure(
        int monitoredAppId,
        DateTime timestamp,
        int responseTimeMs,
        string reason) => new()
        {
            MonitoredAppId = monitoredAppId,
            Timestamp = timestamp,
            IsUp = false,
            ResponseTimeMs = responseTimeMs,
            StatusCode = null,
            Error = reason.Length > AppMonitoringDefaults.MaximumErrorLength
                ? reason[..AppMonitoringDefaults.MaximumErrorLength]
                : reason
        };
}

/// <summary>Global monitoring summary for the dashboard tile.</summary>
public sealed record AppMonitoringSummaryDto
{
    public int TotalCount { get; init; }
    public int UpCount { get; init; }
    public int DownCount { get; init; }
    public int DegradedCount { get; init; }
    public int UnknownCount { get; init; }
    /// <summary>Physical PostgreSQL bytes used by app telemetry tables; observable growth signal.</summary>
    public long TelemetryStorageBytes { get; init; }
    /// <summary>Every enabled monitored application visible to the caller.</summary>
    public IReadOnlyList<MonitoredAppStatusDto> Applications { get; init; } = [];
    /// <summary>Backward-compatible subset retained for clients that only surface unhealthy applications.</summary>
    public IReadOnlyList<MonitoredAppStatusDto> Troubled { get; init; } = [];
    /// <summary>The audience of every application visible to the caller, added up. Null when none
    /// of them measures its audience.</summary>
    public AppAudienceTotalsDto? Audience { get; init; }
}

/// <summary> The audience figures of several applications added up. A sum, not a number of people:
/// the visitor pseudonyms are derived per application, so someone who visits two applications
/// counts once in each. </summary>
public sealed record AppAudienceTotalsDto
{
    /// <summary>Applications that measure their audience and are counted here.</summary>
    public int ApplicationCount { get; init; }
    /// <summary>Sessions seen in the last five minutes.</summary>
    public int OnlineVisitors { get; init; }
    public int VisitorsToday { get; init; }
    public int VisitorsThisWeek { get; init; }
    public int VisitorsThisMonth { get; init; }
    public int AuthenticatedVisitorsThisMonth { get; init; }
    public int SessionsThisMonth { get; init; }
    public long PageViewsThisMonth { get; init; }
}

/// <summary>Compact status line for a monitored application on the dashboard.</summary>
public sealed record MonitoredAppStatusDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public AppHealthStatus CurrentStatus { get; init; }
    public DateTime? LastStatusChangeAt { get; init; }
    /// <summary>Distinct sessions seen in the active window; null when web analytics is unavailable.</summary>
    public int? OnlineVisitorCount { get; init; }

    /// <summary>The latest telemetry of any kind received from the app (OTLP or web analytics);
    /// null when nothing ever arrived. It is what tells "collected, then stopped" apart from "never
    /// configured".</summary>
    public DateTime? LastEventAt { get; init; }
}
