// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>
/// R-458: the hourly metric rollup reads only the hours that still miss rows. R-455: the per-route
/// timings an application exports through Aetheus.Telemetry become its Performance table.
/// </summary>
public sealed class AppMetricRollupAndPerformanceTests : IDisposable
{
    private static readonly DateTime CurrentHour = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly AppDbContext _db;
    private readonly AppMetricRepository _repo;

    public AppMetricRollupAndPerformanceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new AppMetricRepository(_db);
        _db.MonitoredApps.Add(new MonitoredApp { Id = 1, ProjectId = 1, Name = "app" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    /// <summary>The sweep's loop: each completed hour of the lookback, one at a time (R2-020).</summary>
    private async Task<int> AggregateLastHoursAsync(int hours, CancellationToken ct)
    {
        var written = 0;
        for (var hour = CurrentHour.AddHours(-hours); hour < CurrentHour; hour = hour.AddHours(1))
            written += await _repo.AggregateHourAsync(hour, ct);
        return written;
    }

    private static AppMetricSample Sample(DateTime at, string name, double value, string? attributes = null,
        MetricKind kind = MetricKind.Gauge) =>
        new() { MonitoredAppId = 1, Timestamp = at, MetricName = name, Value = value, AttributesJson = attributes, Kind = kind };

    [Fact]
    public async Task R458_OnlyTheHoursMissingRollups_AreAggregated()
    {
        var ct = TestContext.Current.CancellationToken;
        var rolled = CurrentHour.AddHours(-3);
        var missing = CurrentHour.AddHours(-1);
        _db.AppMetricSamples.AddRange(
            Sample(rolled.AddMinutes(10), "cpu", 10),
            Sample(missing.AddMinutes(10), "cpu", 20),
            Sample(missing.AddMinutes(40), "cpu", 40));
        _db.AppMetricHourly.Add(new AppMetricHourly
        {
            MonitoredAppId = 1,
            MetricName = "cpu",
            HourUtc = rolled,
            SampleCount = 1,
            AvgValue = 10,
            MinValue = 10,
            MaxValue = 10,
            P95Value = 10
        });
        await _db.SaveChangesAsync(ct);

        var written = await AggregateLastHoursAsync(6, ct);

        Assert.Equal(1, written);
        var added = await _db.AppMetricHourly.AsNoTracking().SingleAsync(h => h.HourUtc == missing, ct);
        Assert.Equal((2, 30d, 20d, 40d), (added.SampleCount, added.AvgValue, added.MinValue, added.MaxValue));
        // Idempotent: every hour now has its rows.
        Assert.Equal(0, await AggregateLastHoursAsync(6, ct));
    }

    [Fact]
    public async Task R458_ASeriesArrivingLateInARolledUpHour_IsStillAggregated()
    {
        var ct = TestContext.Current.CancellationToken;
        var hour = CurrentHour.AddHours(-2);
        _db.AppMetricSamples.AddRange(Sample(hour.AddMinutes(5), "cpu", 1), Sample(hour.AddMinutes(6), "memory", 7));
        _db.AppMetricHourly.Add(new AppMetricHourly
        {
            MonitoredAppId = 1,
            MetricName = "cpu",
            HourUtc = hour,
            SampleCount = 1,
            AvgValue = 1,
            MinValue = 1,
            MaxValue = 1,
            P95Value = 1
        });
        await _db.SaveChangesAsync(ct);

        Assert.Equal(1, await AggregateLastHoursAsync(6, ct));
        Assert.Equal(7, (await _db.AppMetricHourly.AsNoTracking().SingleAsync(h => h.MetricName == "memory", ct)).AvgValue);
    }

    [Fact]
    public async Task R458_ACounterKeepsItsDeltaAcrossTheHourBoundary_WhenOnlyTheLaterHourIsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var previous = CurrentHour.AddHours(-2);
        var missing = CurrentHour.AddHours(-1);
        _db.AppMetricSamples.AddRange(
            Sample(previous.AddMinutes(50), "requests", 100, kind: MetricKind.Sum),
            Sample(missing.AddMinutes(10), "requests", 130, kind: MetricKind.Sum));
        _db.AppMetricHourly.Add(new AppMetricHourly
        {
            MonitoredAppId = 1,
            MetricName = "requests",
            HourUtc = previous,
            Kind = MetricKind.Sum,
            SampleCount = 1
        });
        await _db.SaveChangesAsync(ct);

        Assert.Equal(1, await AggregateLastHoursAsync(6, ct));
        // The first point of the missing hour gets its delta against the last point of the hour before.
        Assert.Equal(30, (await _db.AppMetricHourly.AsNoTracking().SingleAsync(h => h.HourUtc == missing, ct)).AvgValue);
    }

    [Fact]
    public async Task R455_TheLatestExport_IsTheNewestPointOfEachSeries_NearTheNewestSample()
    {
        var ct = TestContext.Current.CancellationToken;
        var route = """{"http.request.method":"GET","http.route":"api/orders"}""";
        _db.AppMetricSamples.AddRange(
            Sample(CurrentHour.AddMinutes(-10), RequestPerformanceMetrics.P95Metric, 900, route),
            Sample(CurrentHour.AddMinutes(-1), RequestPerformanceMetrics.P95Metric, 120, route),
            Sample(CurrentHour, RequestPerformanceMetrics.P95Metric, 110, route),
            Sample(CurrentHour, RequestPerformanceMetrics.CountMetric, 42, route),
            Sample(CurrentHour, "process.cpu.time", 5, route));
        await _db.SaveChangesAsync(ct);

        var latest = await _repo.GetLatestExportAsync(1, AppRoutePerformance.MetricNames, AppRoutePerformance.LatestPointLookback, ct);

        Assert.Equal(2, latest.Count);
        Assert.Equal(110, latest.Single(sample => sample.MetricName == RequestPerformanceMetrics.P95Metric).Value);
        Assert.Empty(await _repo.GetLatestExportAsync(2, AppRoutePerformance.MetricNames, AppRoutePerformance.LatestPointLookback, ct));
    }

    [Fact]
    public async Task R2082_ARouteIdleSinceItsLastExport_KeepsItsNewestPoint_WithinTheWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var orders = """{"http.request.method":"GET","http.route":"api/orders"}""";
        var login = """{"http.request.method":"POST","http.route":"api/login"}""";
        var gone = """{"http.request.method":"GET","http.route":"api/old"}""";
        _db.AppMetricSamples.AddRange(
            // login was last called three hours ago: no sample of it since (R2-082, idle routes are not exported).
            Sample(CurrentHour.AddHours(-3).AddMinutes(-1), RequestPerformanceMetrics.P95Metric, 700, login),
            Sample(CurrentHour.AddHours(-3), RequestPerformanceMetrics.P95Metric, 650, login),
            Sample(CurrentHour.AddHours(-3), RequestPerformanceMetrics.CountMetric, 9, login),
            Sample(CurrentHour.AddHours(-25), RequestPerformanceMetrics.P95Metric, 5, gone),
            Sample(CurrentHour, RequestPerformanceMetrics.P95Metric, 110, orders),
            Sample(CurrentHour, RequestPerformanceMetrics.CountMetric, 42, orders));
        await _db.SaveChangesAsync(ct);

        var report = AppRoutePerformance.Build(
            await _repo.GetLatestExportAsync(1, AppRoutePerformance.MetricNames, AppRoutePerformance.LatestPointLookback, ct));

        Assert.Equal(["api/login", "api/orders"], report.Routes.Select(route => route.Route).Order(StringComparer.Ordinal));
        var loginLine = report.Routes.Single(route => route.Route == "api/login");
        Assert.Equal((650d, 9L), (loginLine.P95Ms!.Value, loginLine.Count!.Value));
    }

    [Fact]
    public void R455_TheGauges_BecomeOneLinePerRouteAndMethod_SlowestFirst()
    {
        var orders = """{"http.request.method":"GET","http.route":"api/orders"}""";
        var login = """{"http.request.method":"POST","http.route":"api/login"}""";
        var report = AppRoutePerformance.Build(
        [
            Sample(CurrentHour, RequestPerformanceMetrics.CountMetric, 12, orders),
            Sample(CurrentHour, RequestPerformanceMetrics.P50Metric, 30, orders),
            Sample(CurrentHour, RequestPerformanceMetrics.P95Metric, 80, orders),
            Sample(CurrentHour, RequestPerformanceMetrics.P99Metric, 95, orders),
            Sample(CurrentHour, RequestPerformanceMetrics.MaxMetric, 100, orders),
            Sample(CurrentHour.AddSeconds(-1), RequestPerformanceMetrics.P95Metric, 1500, login),
            Sample(CurrentHour, RequestPerformanceMetrics.P95Metric, 5, null),
            Sample(CurrentHour, RequestPerformanceMetrics.P95Metric, 5, "not json")
        ]);

        Assert.Equal(CurrentHour, report.MeasuredAt);
        Assert.Equal(2, report.Routes.Count);
        Assert.Equal(("POST", "api/login", 1500d), (report.Routes[0].Method, report.Routes[0].Route, report.Routes[0].P95Ms!.Value));
        Assert.Null(report.Routes[0].Count);           // not exported: shown as missing, never as zero
        var first = report.Routes[1];
        Assert.Equal((12L, 30d, 80d, 95d, 100d), (first.Count!.Value, first.P50Ms!.Value, first.P95Ms!.Value, first.P99Ms!.Value, first.MaxMs!.Value));
    }

    [Fact]
    public void R476_TheWindowGauge_GivesTheDateTheFiguresCountFrom_AndIsNotARoute()
    {
        var orders = """{"http.request.method":"GET","http.route":"api/orders"}""";
        var report = AppRoutePerformance.Build(
        [
            Sample(CurrentHour, RequestPerformanceMetrics.P95Metric, 80, orders),
            Sample(CurrentHour, RequestPerformanceMetrics.WindowMetric, 5400, null)
        ]);

        Assert.Equal(CurrentHour.AddMinutes(-90), report.WindowSince);
        Assert.Single(report.Routes);
        // A package older than 0.3.0 does not export the window: the date is unknown, not invented.
        Assert.Null(AppRoutePerformance.Build([Sample(CurrentHour, RequestPerformanceMetrics.P95Metric, 80, orders)]).WindowSince);
    }

    [Fact]
    public void R455_NoExport_HasNoMeasurementTime()
    {
        var report = AppRoutePerformance.Build([]);

        Assert.Null(report.MeasuredAt);
        Assert.Empty(report.Routes);
    }
}
