// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Performance;

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>
/// PLAN-003 lot 27 / D27, R-455, decision of 2026-10-07: the administration Performance page reads Aetheus's
/// own request timings from the collector of the OmniEurope.Performance package, the same collector the
/// Aetheus.Telemetry export reads when it is enabled. This class only says which requests Aetheus keeps and
/// maps the package's report to the page's contract.
/// </summary>
public static class ApiPerformanceReport
{
    /// <summary>The Performance page's own route. Its endpoint carries <c>DisableHttpMetrics</c>: reading
    /// the report is never measured, or an open page would reload itself forever on its own traffic.</summary>
    public const string ReportRoute = "api/admin/performance";

    /// <summary>24 hours, API routes only: the static files, the SPA fallback and the health probes are not
    /// the API being timed. The package writes route templates in lower case with a leading slash.</summary>
    public static void Configure(PerformanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Window = TimeSpan.FromHours(24);
        options.RouteFilter = route => route.StartsWith("/api/", StringComparison.Ordinal);
    }

    public static ApiPerformanceReportDto Build(RequestPerformanceRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return ToDto(recorder.Summarize());
    }

    /// <summary>The page shows routes without the package's leading slash, as it always has.</summary>
    public static ApiPerformanceReportDto ToDto(RequestPerformanceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new ApiPerformanceReportDto
        {
            Since = summary.Since,
            SampleCount = summary.SampleCount,
            Truncated = summary.Truncated,
            Slowest = [.. summary.Slowest.Select(sample => new ApiCallTimingDto
            {
                Method = sample.Method,
                Route = sample.Route.TrimStart('/'),
                StatusCode = sample.StatusCode,
                DurationMs = sample.DurationMs,
                At = sample.At
            })],
            Endpoints = [.. summary.Routes.Select(route => new ApiEndpointTimingDto
            {
                Method = route.Method,
                Route = route.Route.TrimStart('/'),
                Count = route.Count,
                P50Ms = route.P50Ms,
                P95Ms = route.P95Ms,
                P99Ms = route.P99Ms,
                MaxMs = route.MaxMs
            })]
        };
    }
}
