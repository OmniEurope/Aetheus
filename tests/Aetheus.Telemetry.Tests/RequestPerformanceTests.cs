// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OmniEurope.Performance;

namespace Aetheus.Telemetry.Tests;

/// <summary>
/// R-455, decision of 2026-10-07: the request timings are measured by the OmniEurope.Performance collector
/// (its arithmetic, window and filters are pinned by that package's own tests); this package exports them
/// per route. Checked here through the gauges' public surface, with the collector fed the way ASP.NET Core
/// feeds it and a clock the test moves. The registration through AddAetheusTelemetry is tested with the
/// other registration tests (they share the AETHEUS_TELEMETRY_ENABLED variable).
/// </summary>
public sealed class RequestPerformanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Metrics_ExportOneSeriesPerRouteAndMethod_WithTheReportFigures_WithoutTheLeadingSlash()
    {
        var clock = new ManualClock(Now);
        using var feed = new Feed(clock);
        using var metrics = new RequestPerformanceMetrics(feed.Recorder, clock);
        foreach (var duration in new double[] { 10, 20, 30, 40 })
            feed.Request("orders/{id}", "GET", 200, duration);
        feed.Request("orders/{id}", "DELETE", 204, 7);

        var p95 = metrics.Measure(route => route.P95Ms).ToList();
        var count = metrics.Measure(route => route.Count).ToList();

        Assert.Equal(2, p95.Count);
        var get = p95.Single(measurement => Tag(measurement, RequestPerformanceMetrics.MethodAttribute) == "GET");
        Assert.Equal(40, get.Value, 3);
        Assert.Equal("orders/{id}", Tag(get, RequestPerformanceMetrics.RouteAttribute));
        Assert.Equal(4, count.Single(measurement => Tag(measurement, RequestPerformanceMetrics.MethodAttribute) == "GET").Value);
        Assert.Equal(1, count.Single(measurement => Tag(measurement, RequestPerformanceMetrics.MethodAttribute) == "DELETE").Value);
    }

    [Fact]
    public void R476_ARouteWrittenInTwoCases_IsOneSeries()
    {
        var clock = new ManualClock(Now);
        using var feed = new Feed(clock);
        using var metrics = new RequestPerformanceMetrics(feed.Recorder, clock);
        feed.Request("api/AppMonitoring/apps/{id}", "GET", 200, 10);
        feed.Request("api/appmonitoring/apps/{id}", "GET", 200, 30);

        var route = Assert.Single(metrics.Measure(route => route.Count));

        Assert.Equal(("api/appmonitoring/apps/{id}", 2d), (Tag(route, RequestPerformanceMetrics.RouteAttribute), route.Value));
    }

    [Fact]
    public void R476_TheWindowGauge_SaysHowLongTheKeptRequestsSpan()
    {
        var clock = new ManualClock(Now);
        using var feed = new Feed(clock);
        using var metrics = new RequestPerformanceMetrics(feed.Recorder, clock);

        Assert.Empty(metrics.MeasureWindow()); // nothing measured yet: no window to state

        feed.Request("orders", "GET", 200, 10);
        clock.Advance(TimeSpan.FromMinutes(90));
        feed.Request("orders", "GET", 200, 12);

        Assert.Equal(90 * 60, metrics.MeasureWindow().Single().Value);
    }

    [Fact]
    public void Metrics_ReadOneReportPerCollection_ThenANewOneOnceTheClockMoves()
    {
        var clock = new ManualClock(Now);
        using var feed = new Feed(clock);
        using var metrics = new RequestPerformanceMetrics(feed.Recorder, clock);
        feed.Request("orders", "GET", 200, 10);

        Assert.Single(metrics.Measure(route => route.MaxMs));
        feed.Request("orders", "GET", 200, 90);
        // Same collection (the clock did not move): the five gauges share the report already built.
        Assert.Equal(10, metrics.Measure(route => route.MaxMs).Single().Value, 3);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(90, metrics.Measure(route => route.MaxMs).Single().Value, 3);
    }

    [Fact]
    public void R2082_ARoute_IsExportedOnlyForACollectionInWhichItsFiguresChanged()
    {
        var clock = new ManualClock(Now);
        using var feed = new Feed(clock);
        using var metrics = new RequestPerformanceMetrics(feed.Recorder, clock);
        feed.Request("orders", "GET", 200, 10);
        feed.Request("login", "POST", 200, 20);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, metrics.Measure(route => route.Count).Count());

        // One minute later only orders was called: login, idle, writes no sample for this collection.
        clock.Advance(TimeSpan.FromMinutes(1));
        feed.Request("orders", "GET", 200, 30);
        clock.Advance(TimeSpan.FromSeconds(1));
        var exported = Assert.Single(metrics.Measure(route => route.Count));
        Assert.Equal(("orders", 2d), (Tag(exported, RequestPerformanceMetrics.RouteAttribute), exported.Value));

        // A minute without any request exports no route at all; the window gauge still says what is kept.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(metrics.Measure(route => route.Count));
        Assert.Single(metrics.MeasureWindow());
    }

    private static string? Tag(Measurement<double> measurement, string key)
    {
        foreach (var tag in measurement.Tags)
        {
            if (tag.Key == key) return tag.Value as string;
        }
        return null;
    }

    /// <summary>The collector fed as ASP.NET Core feeds it: the request-duration histogram of the
    /// <c>Microsoft.AspNetCore.Hosting</c> meter, from the collector's own meter factory, in seconds.</summary>
    private sealed class Feed : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly Histogram<double> _duration;

        public Feed(TimeProvider time)
        {
            _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var meters = _provider.GetRequiredService<IMeterFactory>();
            Recorder = new RequestPerformanceRecorder(
                Options.Create(new PerformanceOptions { Window = RequestPerformanceMetrics.DefaultWindow }), time, meters);
            Recorder.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _duration = meters.Create(RequestPerformanceRecorder.MeterName)
                .CreateHistogram<double>(RequestPerformanceRecorder.InstrumentName);
        }

        public RequestPerformanceRecorder Recorder { get; }

        public void Request(string route, string method, int statusCode, double milliseconds) =>
            _duration.Record(milliseconds / 1000,
                new KeyValuePair<string, object?>("http.route", route),
                new KeyValuePair<string, object?>("http.request.method", method),
                new KeyValuePair<string, object?>("http.response.status_code", statusCode));

        public void Dispose()
        {
            Recorder.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
