// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary> How long a supervised application's routes take to answer, as its
/// <c>Aetheus.Telemetry</c> package (0.2.0 or later) last exported them: the 24 hours of requests
/// the application kept in memory, grouped by route template and method. </summary>
public sealed record AppPerformanceReportDto
{
    /// <summary>When the application last exported these figures. Null when it never did: telemetry off,
    /// or a package older than the per-route export.</summary>
    public DateTime? MeasuredAt { get; init; }

    /// <summary>The oldest request the figures count, as the application said at that export: 24
    /// hours before it at most, later after a restart. Null when the package is older than 1.0.0
    /// and does not say.</summary>
    public DateTime? WindowSince { get; init; }

    /// <summary>One line per route and method, slowest 95th percentile first.</summary>
    public List<AppRouteTimingDto> Routes { get; init; } = [];
}

/// <summary>The figures of one route and method, in milliseconds. A figure the last export did not carry
/// stays null rather than zero.</summary>
public sealed record AppRouteTimingDto
{
    public string Method { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public long? Count { get; init; }
    public double? P50Ms { get; init; }
    public double? P95Ms { get; init; }
    public double? P99Ms { get; init; }
    public double? MaxMs { get; init; }
}
