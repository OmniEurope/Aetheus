// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;
using Aetheus.Telemetry;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary> Turns the latest per-route gauges an application exported through
/// <c>Aetheus.Telemetry</c> (<see cref="RequestPerformanceMetrics"/>) into the table of its
/// Performance tab. Pure, so the grouping is tested without a database. </summary>
public static class AppRoutePerformance
{
    /// <summary>The five gauges, one series each per route and method, and the window's length.</summary>
    public static readonly IReadOnlyList<string> MetricNames =
    [
        RequestPerformanceMetrics.CountMetric,
        RequestPerformanceMetrics.P50Metric,
        RequestPerformanceMetrics.P95Metric,
        RequestPerformanceMetrics.P99Metric,
        RequestPerformanceMetrics.MaxMetric,
        RequestPerformanceMetrics.WindowMetric
    ];

    /// <summary>Each route's latest gauge is found within the default lookback of the newest sample
    /// because idle routes do not export points.</summary>
    public static readonly TimeSpan LatestPointLookback = RequestPerformanceMetrics.DefaultWindow;

    public static AppPerformanceReportDto Build(IReadOnlyList<AppMetricSample> latestSamples)
    {
        ArgumentNullException.ThrowIfNull(latestSamples);
        var routes = new Dictionary<(string Method, string Route), RouteFigures>();
        AppMetricSample? window = null;
        foreach (var sample in latestSamples)
        {
            if (sample.MetricName == RequestPerformanceMetrics.WindowMetric)
            {
                window = sample;
                continue;
            }
            if (ReadRoute(sample.AttributesJson) is not { } key) continue;
            if (!routes.TryGetValue(key, out var figures))
                routes[key] = figures = new RouteFigures();
            figures.Apply(sample.MetricName, sample.Value);
        }

        return new AppPerformanceReportDto
        {
            MeasuredAt = latestSamples.Count == 0 ? null : latestSamples.Max(sample => sample.Timestamp),
            WindowSince = window?.Timestamp.AddSeconds(-window.Value),
            Routes = [.. routes
                .Select(route => route.Value.ToDto(route.Key.Method, route.Key.Route))
                .OrderByDescending(route => route.P95Ms ?? double.MinValue)
                .ThenBy(route => route.Route, StringComparer.Ordinal)
                .ThenBy(route => route.Method, StringComparer.Ordinal)]
        };
    }

    private static (string Method, string Route)? ReadRoute(string? attributesJson)
    {
        if (string.IsNullOrWhiteSpace(attributesJson)) return null;
        Dictionary<string, string>? attributes;
        try
        {
            attributes = JsonSerializer.Deserialize<Dictionary<string, string>>(attributesJson);
        }
        catch (JsonException)
        {
            return null; // mandatory: a malformed attribute set cannot name a route
        }
        return attributes is not null
               && attributes.TryGetValue(RequestPerformanceMetrics.RouteAttribute, out var route)
               && !string.IsNullOrWhiteSpace(route)
            ? (attributes.GetValueOrDefault(RequestPerformanceMetrics.MethodAttribute) ?? string.Empty, route)
            : null;
    }

    private sealed class RouteFigures
    {
        private double? _count;
        private double? _p50;
        private double? _p95;
        private double? _p99;
        private double? _max;

        public void Apply(string metricName, double value)
        {
            switch (metricName)
            {
                case RequestPerformanceMetrics.CountMetric: _count = value; break;
                case RequestPerformanceMetrics.P50Metric: _p50 = value; break;
                case RequestPerformanceMetrics.P95Metric: _p95 = value; break;
                case RequestPerformanceMetrics.P99Metric: _p99 = value; break;
                case RequestPerformanceMetrics.MaxMetric: _max = value; break;
            }
        }

        public AppRouteTimingDto ToDto(string method, string route) => new()
        {
            Method = method,
            Route = route,
            Count = _count is { } count ? (long)Math.Round(count) : null,
            P50Ms = _p50,
            P95Ms = _p95,
            P99Ms = _p99,
            MaxMs = _max
        };
    }
}
