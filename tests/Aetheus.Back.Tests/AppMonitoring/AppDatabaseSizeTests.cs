// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>Show only the latest database-size measurement, largest tables first, with comparison
/// to approximately 24 hours earlier.</summary>
public sealed class AppDatabaseSizeTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private const string Metrics = """{"db.schema.name":"public","db.collection.name":"AnalysisMetrics"}""";
    private const string Samples = """{"db.schema.name":"public","db.collection.name":"AppMetricSamples"}""";
    private const string Dropped = """{"db.schema.name":"public","db.collection.name":"Dropped"}""";
    private const string Database = """{"db.namespace":"aetheus"}""";
    private readonly AppDbContext _db;
    private readonly AppTelemetryService _sut;

    public AppDatabaseSizeTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _sut = new AppTelemetryService(
            new AppMonitoringRepository(_db), new AppMetricRepository(_db),
            new AppLogRepository(_db), new AppErrorRepository(_db), new AppVisitorRepository(_db),
            new IngestKeyHasher(Options.Create(new JwtOptions { SigningKey = "unit-test-signing-key-least-32-bytes!!" })),
            Substitute.For<IIngestService>(), Substitute.For<IAuditService>(),
            new ConfigurationBuilder().Build(), new FakeTimeProvider(new DateTimeOffset(Now)));
        _db.MonitoredApps.Add(new MonitoredApp { Id = 1, ProjectId = 1, Name = "aetheus" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static AppMetricSample Sample(DateTime at, string name, double value, string attributes) =>
        new() { MonitoredAppId = 1, Timestamp = at, MetricName = name, Value = value, AttributesJson = attributes };

    [Fact]
    public async Task TheNewestMeasurement_WithTheSizesOfADayBefore_BecomesTheReport()
    {
        var ct = TestContext.Current.CancellationToken;
        var dayBefore = Now.AddHours(-24);
        _db.AppMetricSamples.AddRange(
            // The newest measurement: the database, two tables and their rows, within seconds of each other.
            Sample(Now, DatabaseSizeMetrics.DatabaseSizeMetric, 6_595_543_040, Database),
            Sample(Now, DatabaseSizeMetrics.TableSizeMetric, 3_197_108_224, Samples),
            Sample(Now.AddSeconds(-2), DatabaseSizeMetrics.TableSizeMetric, 2_542_796_800, Metrics),
            Sample(Now, DatabaseSizeMetrics.TableRowsMetric, 6_631_915, Samples),
            Sample(Now, DatabaseSizeMetrics.TableRowsMetric, 8_418_471, Metrics),
            // An older measurement: a table that has since left the largest ones is not listed.
            Sample(Now.AddMinutes(-15), DatabaseSizeMetrics.TableSizeMetric, 999, Dropped),
            Sample(Now.AddMinutes(-15), DatabaseSizeMetrics.DatabaseSizeMetric, 6_500_000_000, Database),
            // About 24 hours earlier: the newest point of each series in the trend window wins.
            Sample(dayBefore.AddMinutes(-90), DatabaseSizeMetrics.DatabaseSizeMetric, 1, Database),
            Sample(dayBefore.AddMinutes(-15), DatabaseSizeMetrics.DatabaseSizeMetric, 6_000_000_000, Database),
            Sample(dayBefore.AddMinutes(-15), DatabaseSizeMetrics.TableSizeMetric, 3_000_000_000, Samples),
            // Another application's measurement never mixes in.
            new AppMetricSample { MonitoredAppId = 2, Timestamp = Now, MetricName = DatabaseSizeMetrics.DatabaseSizeMetric, Value = 5, AttributesJson = Database });
        await _db.SaveChangesAsync(ct);

        var report = await _sut.GetDatabaseSizeAsync(1, ct);

        Assert.Equal(Now, report.MeasuredAt);
        Assert.Equal("aetheus", report.DatabaseName);
        Assert.Equal(6_595_543_040, report.TotalBytes);
        Assert.Equal(6_000_000_000, report.TotalBytesDayBefore);
        Assert.Equal(["AppMetricSamples", "AnalysisMetrics"], report.Tables.Select(table => table.Name));
        var samples = report.Tables[0];
        Assert.Equal(("public", 3_197_108_224L, 6_631_915L, 3_000_000_000L),
            (samples.Schema, samples.TotalBytes!.Value, samples.EstimatedRows!.Value, samples.TotalBytesDayBefore!.Value));
        Assert.Null(report.Tables[1].TotalBytesDayBefore);
        Assert.Equal(8_418_471, report.Tables[1].EstimatedRows);
    }

    [Fact]
    public async Task WithoutAnyMeasurement_TheReportIsEmpty()
    {
        var report = await _sut.GetDatabaseSizeAsync(1, TestContext.Current.CancellationToken);

        Assert.Null(report.MeasuredAt);
        Assert.Null(report.TotalBytes);
        Assert.Empty(report.Tables);
    }

    [Fact]
    public void AMalformedAttributeSet_NamesNoTable()
    {
        var report = AppDatabaseSize.Build(
            [Sample(Now, DatabaseSizeMetrics.TableSizeMetric, 10, "{not json"), Sample(Now, DatabaseSizeMetrics.TableSizeMetric, 10, "{}")],
            []);

        Assert.Equal(Now, report.MeasuredAt);
        Assert.Empty(report.Tables);
        Assert.Null(report.TotalBytes);
    }
}
