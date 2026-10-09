// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using OmniEurope.Performance;

namespace Aetheus.Telemetry;

/// <summary>
/// Publishes what the OmniEurope.Performance <see cref="RequestPerformanceRecorder"/> measured as OTLP gauges, one series per route
/// template and method: the request count of the window and its 50th, 95th and 99th percentiles and
/// maximum, in milliseconds. One more gauge, without attributes, says how long that window really is.
/// They ride the metrics export <c>AddAetheusTelemetry</c> configures, so Aetheus shows the same
/// per-route table for every application that uses the package.
///
/// The slowest individual calls are not exported: one series per request would be unbounded.
///
/// Recette R2-082: a route is exported only for a collection in which its figures changed (it served a
/// request, or older requests left the window). Repeating unchanged figures every minute for every route
/// ever called was most of the rows Aetheus stored about itself. A reader takes each route's newest point,
/// whenever it was written. The package keeps no per-request list to read, so the change is told from the
/// route's figures, compared with the previous collection's.
///
/// Routes are exported without the leading slash the package writes (<c>api/orders/{id}</c>), the form
/// Aetheus has always stored.
/// </summary>
public sealed class RequestPerformanceMetrics : IHostedService, IDisposable
{
    public const string MeterName = "Aetheus.Telemetry.Performance";
    public const string CountMetric = "aetheus.http.server.request.count";
    public const string P50Metric = "aetheus.http.server.request.duration.p50";
    public const string P95Metric = "aetheus.http.server.request.duration.p95";
    public const string P99Metric = "aetheus.http.server.request.duration.p99";
    public const string MaxMetric = "aetheus.http.server.request.duration.max";

    /// <summary>Recette R-476: seconds between the oldest request kept and the report. The window holds
    /// 24 hours at most and restarts with the process, so the figures of a freshly started application
    /// cover minutes, not a day; this gauge lets the reader see which.</summary>
    public const string WindowMetric = "aetheus.http.server.request.window";
    /// <summary>The window the figures cover unless the host chose another: 24 hours. Aetheus searches
    /// each route's newest point within it.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);
    public const string RouteAttribute = "http.route";
    public const string MethodAttribute = "http.request.method";

    /// <summary>One collection reads the five gauges one after the other: they share one report.</summary>
    private static readonly TimeSpan ReportReuse = TimeSpan.FromSeconds(1);

    private readonly RequestPerformanceRecorder _recorder;
    private readonly TimeProvider _time;
    private readonly Meter _meter = new(MeterName);
    private readonly object _gate = new();
    private RequestPerformanceSummary? _report;
    private DateTimeOffset _reportAt;
    private IReadOnlySet<(string Method, string Route)> _activeRoutes = new HashSet<(string, string)>();
    private Dictionary<(string Method, string Route), RouteTimingSummary> _previous = [];

    public RequestPerformanceMetrics(RequestPerformanceRecorder recorder, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(time);
        _recorder = recorder;
        _time = time;
        _meter.CreateObservableGauge(CountMetric, () => Measure(route => route.Count), "{request}",
            "Requests of the route measured over the measurement window (24 hours by default).");
        _meter.CreateObservableGauge(P50Metric, () => Measure(route => route.P50Ms), "ms",
            "Median request duration of the route over the measurement window (24 hours by default).");
        _meter.CreateObservableGauge(P95Metric, () => Measure(route => route.P95Ms), "ms",
            "95th percentile request duration of the route over the measurement window (24 hours by default).");
        _meter.CreateObservableGauge(P99Metric, () => Measure(route => route.P99Ms), "ms",
            "99th percentile request duration of the route over the measurement window (24 hours by default).");
        _meter.CreateObservableGauge(MaxMetric, () => Measure(route => route.MaxMs), "ms",
            "Longest request duration of the route over the measurement window (24 hours by default).");
        _meter.CreateObservableGauge(WindowMetric, MeasureWindow, "s",
            "Seconds the measured requests span: the window at most, less after a restart.");
    }

    /// <summary>The meter exists from construction; hosting it only makes the container build it at start.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _meter.Dispose();

    /// <summary>The current values of one gauge, one measurement per route and method that served a
    /// request since the previous collection.</summary>
    public IEnumerable<Measurement<double>> Measure(Func<RouteTimingSummary, double> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        IReadOnlySet<(string Method, string Route)> active;
        RequestPerformanceSummary report;
        lock (_gate)
        {
            report = CurrentReport();
            active = _activeRoutes;
        }
        return report.Routes
            .Where(route => active.Contains((route.Method, route.Route)))
            .Select(route => new Measurement<double>(
                value(route),
                new KeyValuePair<string, object?>(RouteAttribute, route.Route.TrimStart('/')),
                new KeyValuePair<string, object?>(MethodAttribute, route.Method)))
            .ToList();
    }

    /// <summary>How long the kept requests span, in seconds; no measurement without a request.</summary>
    public IEnumerable<Measurement<double>> MeasureWindow()
    {
        lock (_gate)
        {
            return CurrentReport().Since is { } oldest
                ? [new Measurement<double>(Math.Max(0, (_reportAt.UtcDateTime - oldest).TotalSeconds))]
                : [];
        }
    }

    private RequestPerformanceSummary CurrentReport()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_report is null || now - _reportAt >= ReportReuse)
            {
                var report = _recorder.Summarize();
                // A route is active when it is new or any of its figures moved since the previous collection.
                _activeRoutes = report.Routes
                    .Where(route => !_previous.TryGetValue((route.Method, route.Route), out var before) || before != route)
                    .Select(route => (route.Method, route.Route))
                    .ToHashSet();
                _previous = report.Routes.ToDictionary(route => (route.Method, route.Route));
                _report = report;
                _reportAt = now;
            }
            return _report;
        }
    }
}
