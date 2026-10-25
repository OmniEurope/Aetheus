// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

public class AppTelemetryServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AppTelemetryService _sut;
    private readonly IIngestService _ingestService = Substitute.For<IIngestService>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 10, 12, 30, 0, TimeSpan.Zero));

    public AppTelemetryServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
        var hasher = new IngestKeyHasher(Options.Create(new JwtOptions { SigningKey = "unit-test-signing-key-least-32-bytes!!" }));
        _sut = new AppTelemetryService(
            new AppMonitoringRepository(_db), new AppMetricRepository(_db),
            new AppLogRepository(_db), new AppErrorRepository(_db), new AppVisitorRepository(_db),
            hasher, _ingestService, Substitute.For<IAuditService>(),
            new ConfigurationBuilder().Build(), _time);
        _db.MonitoredApps.Add(new MonitoredApp { Id = 1, ProjectId = 1, Name = "app" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetMetricSeries_RawWindow_DecimatesToBoundedPointCount()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        for (var i = 0; i < 5000; i++)
            _db.AppMetricSamples.Add(new AppMetricSample { MonitoredAppId = 1, MetricName = "cpu", Timestamp = now.AddSeconds(-i), Value = i });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var series = await _sut.GetMetricSeriesAsync(1, "cpu", null, 24, ct: TestContext.Current.CancellationToken);

        Assert.InRange(series.Points.Count, 1, 1500);
    }

    [Fact]
    public async Task GetMetricSeries_LongWindow_AppendsRecentRawTailPastRollup()
    {
        var now = _time.GetUtcNow().UtcDateTime; // 2026-01-10 12:30
        // One rolled-up hour at 08:00 plus a raw sample in the current (not-yet-aggregated) hour.
        _db.AppMetricHourly.Add(new AppMetricHourly
        {
            MonitoredAppId = 1,
            MetricName = "cpu",
            HourUtc = now.Date.AddHours(8),
            SampleCount = 10,
            MinValue = 1,
            MaxValue = 9,
            AvgValue = 5,
            P95Value = 8
        });
        _db.AppMetricSamples.Add(new AppMetricSample { MonitoredAppId = 1, MetricName = "cpu", Timestamp = now.AddMinutes(-5), Value = 42 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var series = await _sut.GetMetricSeriesAsync(1, "cpu", null, 24 * 7, ct: TestContext.Current.CancellationToken);

        // The recent raw point must survive even though it is newer than the last rollup hour.
        Assert.Contains(series.Points, p => p.Value == 42);
        Assert.Contains(series.Points, p => p.P95 == 8); // the rollup point is still there too
    }

    [Fact]
    public async Task GenerateIngestKey_RotatesPersistedHash_AndInvalidatesOldAndNewCacheEntries()
    {
        var app = await _db.MonitoredApps.SingleAsync(a => a.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        app.IngestKeyHash = "old-hash";
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var generated = await _sut.GenerateIngestKeyAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(generated);
        Assert.False(string.IsNullOrWhiteSpace(generated.Key));
        Assert.Equal(_time.GetUtcNow().UtcDateTime, generated.CreatedAt);
        await _db.Entry(app).ReloadAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(app.IngestKeyHash);
        Assert.NotEqual("old-hash", app.IngestKeyHash);
        Assert.Equal(generated.CreatedAt, app.IngestKeyCreatedAt);
        Assert.Equal(generated.CreatedAt.AddDays(90), app.IngestKeyExpiresAt);
        _ingestService.Received(1).InvalidateKeyCache("old-hash");
        _ingestService.Received(1).InvalidateKeyCache(app.IngestKeyHash!);
        Assert.Null(await _sut.GenerateIngestKeyAsync(999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GenerateIngestKey_AcceptsPreviousKeyOnlyDuringSevenDayOverlap()
    {
        var app = await _db.MonitoredApps.SingleAsync(
            item => item.Id == 1,
            TestContext.Current.CancellationToken);
        app.Enabled = true;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await _sut.GenerateIngestKeyAsync(1, TestContext.Current.CancellationToken);
        var firstHash = app.IngestKeyHash!;

        _time.Advance(TimeSpan.FromDays(1));
        await _sut.GenerateIngestKeyAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(firstHash, app.PreviousIngestKeyHash);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddDays(7), app.PreviousIngestKeyValidUntil);
        Assert.Equal(2, app.IngestKeyVersion);
        var repository = new AppMonitoringRepository(_db);
        var duringOverlap = await repository.ResolveIngestKeyHashAsync(
            firstHash,
            _time.GetUtcNow().UtcDateTime.AddDays(6),
            TestContext.Current.CancellationToken);
        var afterOverlap = await repository.ResolveIngestKeyHashAsync(
            firstHash,
            _time.GetUtcNow().UtcDateTime.AddDays(8),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, duringOverlap?.AppId);
        Assert.Equal(app.PreviousIngestKeyValidUntil, duringOverlap?.ValidUntilUtc);
        Assert.Null(afterOverlap);
    }

    [Fact]
    public async Task R2_013_AManualRotation_KeepsNoKeyOlderThanThePreviousOne()
    {
        // Only a deployment rotation keeps a second previous key (two blue-green colours); an operator
        // rotating, e.g. after a leak, must not keep one more old key alive than before.
        var app = await _db.MonitoredApps.SingleAsync(a => a.Id == 1, TestContext.Current.CancellationToken);
        app.Enabled = true;
        app.IngestKeyHash = "current";
        app.PreviousIngestKeyHash = "previous";
        app.PreviousIngestKeyValidUntil = _time.GetUtcNow().UtcDateTime.AddDays(3);
        app.SecondPreviousIngestKeyHash = "second-previous";
        app.SecondPreviousIngestKeyValidUntil = _time.GetUtcNow().UtcDateTime.AddDays(2);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _sut.GenerateIngestKeyAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal("current", app.PreviousIngestKeyHash);
        Assert.Null(app.SecondPreviousIngestKeyHash);
        Assert.Null(app.SecondPreviousIngestKeyValidUntil);
        Assert.Null(await new AppMonitoringRepository(_db).ResolveIngestKeyHashAsync(
            "previous", _time.GetUtcNow().UtcDateTime, TestContext.Current.CancellationToken));
        _ingestService.Received(1).InvalidateKeyCache("previous");
        _ingestService.Received(1).InvalidateKeyCache("second-previous");
    }

    [Fact]
    public async Task R2_013_Revoke_AlsoClearsTheSecondPreviousKey()
    {
        var app = await _db.MonitoredApps.SingleAsync(a => a.Id == 1, TestContext.Current.CancellationToken);
        app.IngestKeyHash = "current";
        app.SecondPreviousIngestKeyHash = "second-previous";
        app.SecondPreviousIngestKeyValidUntil = _time.GetUtcNow().UtcDateTime.AddDays(2);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(await _sut.RevokeIngestKeyAsync(1, TestContext.Current.CancellationToken));

        await _db.Entry(app).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Null(app.SecondPreviousIngestKeyHash);
        Assert.Null(app.SecondPreviousIngestKeyValidUntil);
        _ingestService.Received(1).InvalidateKeyCache("second-previous");
    }

    [Fact]
    public async Task RevokeIngestKey_ClearsPersistenceAndInvalidatesRevokedHash()
    {
        var app = await _db.MonitoredApps.SingleAsync(a => a.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        app.IngestKeyHash = "revoked-hash";
        app.IngestKeyCreatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _sut.RevokeIngestKeyAsync(1, ct: TestContext.Current.CancellationToken));

        await _db.Entry(app).ReloadAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(app.IngestKeyHash);
        Assert.Null(app.IngestKeyCreatedAt);
        Assert.Null(app.IngestKeyExpiresAt);
        _ingestService.Received(1).InvalidateKeyCache("revoked-hash");
        Assert.False(await _sut.RevokeIngestKeyAsync(999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MetricNamesAndRawSeries_MapDistinctNamesUnitAndLatestPoint()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _db.AppMetricSamples.AddRange(
            new AppMetricSample { MonitoredAppId = 1, MetricName = "cpu", Timestamp = now.AddMinutes(-2), Value = 20, Unit = "%" },
            new AppMetricSample { MonitoredAppId = 1, MetricName = "cpu", Timestamp = now.AddMinutes(-1), Value = 30, Unit = "%" },
            new AppMetricSample { MonitoredAppId = 1, MetricName = "memory", Timestamp = now, Value = 1024, Unit = "MiB" });
        // The names come from their own table, which the ingestion feeds.
        _db.AppMetricNames.AddRange(
            new AppMetricName { MonitoredAppId = 1, Name = "cpu" },
            new AppMetricName { MonitoredAppId = 1, Name = "memory" },
            new AppMetricName { MonitoredAppId = 2, Name = "other-app-metric" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var names = await _sut.GetMetricNamesAsync(1, ct: TestContext.Current.CancellationToken);
        var series = await _sut.GetMetricSeriesAsync(1, "cpu", null, 0, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["cpu", "memory"], names.OrderBy(name => name));
        Assert.Equal("%", series.Unit);
        Assert.Equal([20d, 30d], series.Points.Select(point => point.Value));
    }

    [Fact]
    public async Task GetMetricSeries_AttributesFilter_KeepsDistinctAttributeSeriesSeparate()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _db.AppMetricSamples.AddRange(
            new AppMetricSample { MonitoredAppId = 1, MetricName = "http.duration", Timestamp = now.AddMinutes(-2), Value = 10, AttributesJson = "{\"route\":\"/a\"}" },
            new AppMetricSample { MonitoredAppId = 1, MetricName = "http.duration", Timestamp = now.AddMinutes(-1), Value = 900, AttributesJson = "{\"route\":\"/b\"}" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var groups = await _sut.GetMetricSeriesGroupsAsync(1, "http.duration", TestContext.Current.CancellationToken);
        var seriesA = await _sut.GetMetricSeriesAsync(1, "http.duration", "{\"route\":\"/a\"}", 24, ct: TestContext.Current.CancellationToken);
        var seriesB = await _sut.GetMetricSeriesAsync(1, "http.duration", "{\"route\":\"/b\"}", 24, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, groups.Count);
        Assert.Equal([10d], seriesA.Points.Select(p => p.Value));
        Assert.Equal([900d], seriesB.Points.Select(p => p.Value));
    }

    [Fact]
    public async Task GetMetricSeriesGroups_PutTheMostRecentlyReportedSeriesFirst()
    {
        // Production, 2026-09-12: the chart opens on the first group, and the alphabetical order put an
        // attribute-less group from old samples first, so 27 of 44 metrics opened on "no data".
        var now = _time.GetUtcNow().UtcDateTime;
        _db.AppMetricHourly.Add(new AppMetricHourly
        {
            MonitoredAppId = 1,
            MetricName = "cpu.time",
            AttributesJson = null,
            HourUtc = now.Date.AddDays(-20),
            SampleCount = 1,
            AvgValue = 1
        });
        _db.AppMetricSamples.AddRange(
            new AppMetricSample { MonitoredAppId = 1, MetricName = "cpu.time", Timestamp = now.AddDays(-3), Value = 1, AttributesJson = "{\"cpu.mode\":\"idle\"}" },
            new AppMetricSample { MonitoredAppId = 1, MetricName = "cpu.time", Timestamp = now.AddMinutes(-1), Value = 2, AttributesJson = "{\"cpu.mode\":\"user\"}" },
            new AppMetricSample { MonitoredAppId = 1, MetricName = "cpu.time", Timestamp = now.AddMinutes(-2), Value = 3, AttributesJson = "{\"cpu.mode\":\"system\"}" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var groups = await _sut.GetMetricSeriesGroupsAsync(1, "cpu.time", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["{\"cpu.mode\":\"user\"}", "{\"cpu.mode\":\"system\"}", "{\"cpu.mode\":\"idle\"}", null],
            groups);
    }

    [Fact]
    public async Task GetMetricSeries_RawWindow_CumulativeSumBecomesDeltaAndDropsCounterReset()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _db.AppMetricSamples.AddRange(
            new AppMetricSample { MonitoredAppId = 1, MetricName = "http.requests", Timestamp = now.AddMinutes(-3), Value = 100, Kind = MetricKind.Sum },
            new AppMetricSample { MonitoredAppId = 1, MetricName = "http.requests", Timestamp = now.AddMinutes(-2), Value = 140, Kind = MetricKind.Sum },
            // process restart: counter resets to a small cumulative value - must not surface as a -130 dip.
            new AppMetricSample { MonitoredAppId = 1, MetricName = "http.requests", Timestamp = now.AddMinutes(-1), Value = 5, Kind = MetricKind.Sum });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var series = await _sut.GetMetricSeriesAsync(1, "http.requests", null, 24, ct: TestContext.Current.CancellationToken);

        // First point has no predecessor (dropped); the reset interval is dropped too; only the 100->140 delta survives.
        Assert.Equal([40d], series.Points.Select(p => p.Value));
    }

    [Fact]
    public async Task ThresholdLifecycle_MapsPersistsAndDeletesObservableValues()
    {
        var created = await _sut.CreateThresholdAsync(1, new CreateMetricThresholdRequest
        {
            MetricName = "  cpu.utilization  ",
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 85,
            Enabled = true
        }, ct: TestContext.Current.CancellationToken);

        var listed = await _sut.GetThresholdsAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.Equal("cpu.utilization", created.MetricName);
        Assert.Equal(85, created.Threshold);
        Assert.Contains(listed, item => item.Id == created.Id && item.Operator == ComparisonOperator.GreaterThan);
        Assert.Equal(1, await _sut.GetThresholdAppIdAsync(created.Id, ct: TestContext.Current.CancellationToken));
        Assert.True(await _sut.DeleteThresholdAsync(created.Id, ct: TestContext.Current.CancellationToken));
        Assert.Empty(await _sut.GetThresholdsAsync(1, ct: TestContext.Current.CancellationToken));
        Assert.False(await _sut.DeleteThresholdAsync(999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetVisitorSeries_FillsMissingDays_AndCountsDailyIdentities()
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        _db.AppVisitorIdentities.AddRange(
            new AppVisitorIdentity { MonitoredAppId = 1, DayUtc = today, FingerprintHash = "a", FirstSeenAt = _time.GetUtcNow().UtcDateTime },
            new AppVisitorIdentity { MonitoredAppId = 1, DayUtc = today, FingerprintHash = "b", FirstSeenAt = _time.GetUtcNow().UtcDateTime },
            new AppVisitorIdentity { MonitoredAppId = 1, DayUtc = today.AddDays(-2), FingerprintHash = "c", FirstSeenAt = _time.GetUtcNow().UtcDateTime.AddDays(-2) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var series = await _sut.GetVisitorSeriesAsync(1, 3, TestContext.Current.CancellationToken);

        Assert.Equal(2, series.Today);
        Assert.Equal([1, 0, 2], series.Points.Select(point => point.UniqueVisitors));
    }

    [Fact]
    public async Task AggregateRawIntoHourly_GroupsByAttributesAndDeltaTransformsSumBeforeRollup()
    {
        var hourStart = _time.GetUtcNow().UtcDateTime.Date.AddHours(8);
        _db.AppMetricSamples.AddRange(
            new AppMetricSample { MonitoredAppId = 1, MetricName = "http.requests", Timestamp = hourStart.AddMinutes(1), Value = 100, Kind = MetricKind.Sum, AttributesJson = "{\"route\":\"/a\"}" },
            new AppMetricSample { MonitoredAppId = 1, MetricName = "http.requests", Timestamp = hourStart.AddMinutes(2), Value = 150, Kind = MetricKind.Sum, AttributesJson = "{\"route\":\"/a\"}" },
            new AppMetricSample { MonitoredAppId = 1, MetricName = "http.requests", Timestamp = hourStart.AddMinutes(1), Value = 5, Kind = MetricKind.Sum, AttributesJson = "{\"route\":\"/b\"}" },
            new AppMetricSample { MonitoredAppId = 1, MetricName = "http.requests", Timestamp = hourStart.AddMinutes(2), Value = 12, Kind = MetricKind.Sum, AttributesJson = "{\"route\":\"/b\"}" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var metricRepo = new AppMetricRepository(_db);

        var inserted = await metricRepo.AggregateHourAsync(hourStart, TestContext.Current.CancellationToken);

        Assert.Equal(2, inserted);
        var rows = await _db.AppMetricHourly.ToListAsync(TestContext.Current.CancellationToken);
        var routeA = rows.Single(r => r.AttributesJson == "{\"route\":\"/a\"}");
        var routeB = rows.Single(r => r.AttributesJson == "{\"route\":\"/b\"}");
        Assert.Equal(50, routeA.AvgValue); // delta 150-100, not the raw cumulative values
        Assert.Equal(7, routeB.AvgValue); // delta 12-5
        Assert.Equal(MetricKind.Sum, routeA.Kind);
    }

    [Fact]
    public async Task LogsAndErrors_ClampPaginationAndMapStoredTelemetry()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _db.AppLogEntries.Add(new AppLogEntry
        {
            MonitoredAppId = 1,
            Timestamp = now.AddMinutes(-2),
            SeverityNumber = 17,
            SeverityText = "ERROR",
            Body = "database unavailable",
            AttributesJson = "{\"node\":\"db-1\"}"
        });
        _db.AppErrorEvents.Add(new AppErrorEvent
        {
            Id = 14,
            MonitoredAppId = 1,
            Fingerprint = "fp",
            ExceptionType = "TimeoutException",
            Message = "probe timed out",
            TopFrame = "Probe.Run",
            OccurrenceCount = 3,
            FirstSeenAt = now.AddHours(-1),
            LastSeenAt = now
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var logs = await _sut.GetLogsAsync(1, hours: 500, minSeverity: 10, search: "database", page: 0, pageSize: 500, ct: TestContext.Current.CancellationToken);
        var errors = await _sut.GetErrorsAsync(1, page: 0, pageSize: 500, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, logs.Page);
        Assert.Equal(200, logs.PageSize);
        var log = Assert.Single(logs.Items);
        Assert.Equal("database unavailable", log.Body);
        Assert.Equal("ERROR", log.SeverityText);
        Assert.Equal(1, errors.Page);
        Assert.Equal(200, errors.PageSize);
        var error = Assert.Single(errors.Items);
        Assert.Equal(14, error.Id);
        Assert.Equal(3, error.OccurrenceCount);
        Assert.Equal("Probe.Run", error.TopFrame);
    }

    // --- sort and header filters of the log grid ---

    private async Task SeedGridLogsAsync()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _db.AppLogEntries.AddRange(
            new AppLogEntry { MonitoredAppId = 1, Timestamp = now.AddMinutes(-30), SeverityNumber = 9, SeverityText = "Information", Body = "Request served", AttributesJson = "{\"route\":\"/home\"}" },
            new AppLogEntry { MonitoredAppId = 1, Timestamp = now.AddMinutes(-20), SeverityNumber = 17, SeverityText = "Error", Body = "Database unavailable", AttributesJson = "{\"node\":\"db-1\"}" },
            new AppLogEntry { MonitoredAppId = 1, Timestamp = now.AddMinutes(-10), SeverityNumber = 13, SeverityText = "Warning", Body = "Slow query", AttributesJson = null },
            new AppLogEntry { MonitoredAppId = 1, Timestamp = now.AddMinutes(-5), SeverityNumber = 5, SeverityText = "Debug", Body = "Cache hit" },
            new AppLogEntry { MonitoredAppId = 2, Timestamp = now.AddMinutes(-1), SeverityNumber = 17, Body = "Other app error" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private Task<PaginatedResult<AppLogEntryDto>> GridLogsAsync(
        IReadOnlyList<GridFilter>? filters = null, string? sortBy = null, bool sortDescending = true) =>
        _sut.GetLogsAsync(1, hours: 24, minSeverity: null, search: null, page: 1, pageSize: 50,
            ct: TestContext.Current.CancellationToken, filters: filters, sortBy: sortBy, sortDescending: sortDescending);

    [Fact]
    public async Task R358_Logs_WithoutSort_AreNewestFirst_AndCarryTheirId()
    {
        await SeedGridLogsAsync();

        var logs = await GridLogsAsync();

        Assert.Equal(["Cache hit", "Slow query", "Database unavailable", "Request served"], logs.Items.Select(l => l.Body));
        Assert.Equal(4, logs.TotalCount);
        Assert.All(logs.Items, l => Assert.True(l.Id > 0));
    }

    [Fact]
    public async Task R358_Logs_SortOnSeverityAndMessage_OrderTheWholeLog()
    {
        await SeedGridLogsAsync();

        var bySeverity = await GridLogsAsync(sortBy: "Severity", sortDescending: false);
        var byMessageDescending = await GridLogsAsync(sortBy: "Body", sortDescending: true);
        var oldestFirst = await GridLogsAsync(sortBy: "Timestamp", sortDescending: false);

        Assert.Equal([5, 9, 13, 17], bySeverity.Items.Select(l => l.SeverityNumber));
        Assert.Equal(["Slow query", "Request served", "Database unavailable", "Cache hit"], byMessageDescending.Items.Select(l => l.Body));
        Assert.Equal("Request served", oldestFirst.Items[0].Body);
    }

    [Fact]
    public async Task R358_Logs_SeverityFilter_MatchesTheClassesTicked_AndTheCountFollows()
    {
        await SeedGridLogsAsync();

        var warnOrError = await GridLogsAsync(filters:
        [
            new GridFilter { Field = "Severity", Operator = GridFilterOperator.In, Value = $"WARN{GridFilter.ListSeparator}error" }
        ]);
        var notDebug = await GridLogsAsync(filters:
        [
            new GridFilter { Field = "severity", Operator = GridFilterOperator.NotIn, Value = "DEBUG" }
        ]);

        Assert.Equal(["Slow query", "Database unavailable"], warnOrError.Items.Select(l => l.Body));
        Assert.Equal(2, warnOrError.TotalCount);
        Assert.Equal(3, notDebug.TotalCount);
        Assert.DoesNotContain(notDebug.Items, l => l.SeverityNumber == 5);
    }

    [Fact]
    public async Task R358_Logs_TextAndTimeFilters_ApplyOnTheServer()
    {
        await SeedGridLogsAsync();
        var now = _time.GetUtcNow().UtcDateTime;

        var byMessage = await GridLogsAsync(filters: [new GridFilter { Field = "Body", Operator = GridFilterOperator.Contains, Value = "DATABASE" }]);
        var byAttributes = await GridLogsAsync(filters: [new GridFilter { Field = "AttributesJson", Operator = GridFilterOperator.Contains, Value = "route" }]);
        var lastQuarter = await GridLogsAsync(filters:
        [
            new GridFilter
            {
                Field = "Timestamp",
                Operator = GridFilterOperator.GreaterThanOrEqual,
                Value = now.AddMinutes(-15).ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            }
        ]);

        Assert.Equal("Database unavailable", Assert.Single(byMessage.Items).Body);
        Assert.Equal("Request served", Assert.Single(byAttributes.Items).Body);
        Assert.Equal(["Cache hit", "Slow query"], lastQuarter.Items.Select(l => l.Body));
        Assert.Equal(2, lastQuarter.TotalCount);
    }

    [Theory]
    [InlineData("MonitoredAppId")]
    [InlineData("AttributesJson")]
    [InlineData("Body; DROP TABLE AppLogEntries")]
    public async Task R358_Logs_SortOnAColumnOutsideTheAllowList_IsABadRequest(string sortBy)
    {
        await SeedGridLogsAsync();

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => GridLogsAsync(sortBy: sortBy));
    }

    [Fact]
    public async Task R358_Logs_UnknownFilterColumnOrSeverityClass_IsABadRequest()
    {
        await SeedGridLogsAsync();

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => GridLogsAsync(filters:
            [new GridFilter { Field = "MonitoredAppId", Operator = GridFilterOperator.Equals, Value = "2" }]));
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => GridLogsAsync(filters:
            [new GridFilter { Field = "Severity", Operator = GridFilterOperator.In, Value = "LOUD" }]));
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => GridLogsAsync(filters:
            [new GridFilter { Field = "Severity", Operator = GridFilterOperator.Contains, Value = "ERR" }]));
    }

    // Sort and header filters of the error grid follow the log-grid contract.

    private async Task SeedGridErrorsAsync()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _db.AppErrorEvents.AddRange(
            new AppErrorEvent { Id = 21, MonitoredAppId = 1, Fingerprint = "a", ExceptionType = "TimeoutException", Message = "probe timed out", OccurrenceCount = 3, FirstSeenAt = now.AddDays(-3), LastSeenAt = now.AddHours(-1) },
            new AppErrorEvent { Id = 22, MonitoredAppId = 1, Fingerprint = "b", ExceptionType = "NullReferenceException", Message = "object reference not set", OccurrenceCount = 40, FirstSeenAt = now.AddDays(-2), LastSeenAt = now.AddMinutes(-5) },
            new AppErrorEvent { Id = 23, MonitoredAppId = 1, Fingerprint = "c", ExceptionType = "TimeoutException", Message = "database timed out", OccurrenceCount = 7, FirstSeenAt = now.AddDays(-1), LastSeenAt = now.AddHours(-1) },
            new AppErrorEvent { Id = 24, MonitoredAppId = 1, Fingerprint = "d", ExceptionType = "IOException", Message = "disk full", OccurrenceCount = 1, FirstSeenAt = now.AddHours(-30), LastSeenAt = now.AddHours(-20) },
            new AppErrorEvent { Id = 25, MonitoredAppId = 2, Fingerprint = "e", ExceptionType = "OtherAppException", Message = "other app", OccurrenceCount = 99, FirstSeenAt = now, LastSeenAt = now });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private Task<PaginatedResult<AppErrorEventDto>> GridErrorsAsync(
        IReadOnlyList<GridFilter>? filters = null, string? sortBy = null, bool sortDescending = true, int page = 1, int pageSize = 50) =>
        _sut.GetErrorsAsync(1, page, pageSize, TestContext.Current.CancellationToken, filters, sortBy, sortDescending);

    [Fact]
    public async Task Errors_WithoutSort_AreMostRecentlySeenFirst_ThenByIdDescending()
    {
        await SeedGridErrorsAsync();

        var errors = await GridErrorsAsync();

        // 21 and 23 share their last-seen time: the id breaks the tie, so pages never overlap.
        Assert.Equal([22, 23, 21, 24], errors.Items.Select(e => e.Id));
        Assert.Equal(4, errors.TotalCount);
    }

    [Fact]
    public async Task Errors_SortOnEveryAllowedColumn_OrdersEveryGroupOfTheApp()
    {
        await SeedGridErrorsAsync();

        var byCount = await GridErrorsAsync(sortBy: "OccurrenceCount", sortDescending: false);
        var byType = await GridErrorsAsync(sortBy: "ExceptionType", sortDescending: false);
        var byMessage = await GridErrorsAsync(sortBy: "Message", sortDescending: true);
        var byFirstSeen = await GridErrorsAsync(sortBy: "FirstSeenAt", sortDescending: false);
        var byLastSeenAscending = await GridErrorsAsync(sortBy: "lastSeenAt", sortDescending: false);
        var secondPageByCount = await GridErrorsAsync(sortBy: "OccurrenceCount", sortDescending: true, page: 2, pageSize: 2);

        Assert.Equal([24, 21, 23, 22], byCount.Items.Select(e => e.Id));
        // Same type: the secondary order (last seen, then id, both descending) decides.
        Assert.Equal([24, 22, 23, 21], byType.Items.Select(e => e.Id));
        Assert.Equal([21, 22, 24, 23], byMessage.Items.Select(e => e.Id));
        Assert.Equal([21, 22, 24, 23], byFirstSeen.Items.Select(e => e.Id));
        Assert.Equal([24, 23, 21, 22], byLastSeenAscending.Items.Select(e => e.Id));
        Assert.Equal([21, 24], secondPageByCount.Items.Select(e => e.Id));
        Assert.Equal(4, secondPageByCount.TotalCount);
    }

    [Fact]
    public async Task Errors_HeaderFilters_ApplyOnTheServer_BeforeTheCount()
    {
        await SeedGridErrorsAsync();
        var now = _time.GetUtcNow().UtcDateTime;
        string Iso(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        var byTypes = await GridErrorsAsync(filters:
            [new GridFilter { Field = "ExceptionType", Operator = GridFilterOperator.In, Value = $"timeoutexception{GridFilter.ListSeparator}IOException" }]);
        var byMessage = await GridErrorsAsync(filters:
            [new GridFilter { Field = "Message", Operator = GridFilterOperator.Contains, Value = "TIMED OUT" }]);
        var frequent = await GridErrorsAsync(filters:
            [new GridFilter { Field = "OccurrenceCount", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "5" }]);
        var firstSeenRange = await GridErrorsAsync(filters:
        [
            new GridFilter
            {
                Field = "FirstSeenAt", Operator = GridFilterOperator.GreaterThanOrEqual, Value = Iso(now.AddDays(-2).AddMinutes(-1)),
                SecondOperator = GridFilterOperator.LessThan, SecondValue = Iso(now.AddHours(-12)), Logic = GridFilterLogic.And
            }
        ]);
        var seenLastHalfHour = await GridErrorsAsync(filters:
            [new GridFilter { Field = "LastSeenAt", Operator = GridFilterOperator.GreaterThanOrEqual, Value = Iso(now.AddMinutes(-30)) }]);
        var combined = await GridErrorsAsync(filters:
        [
            new GridFilter { Field = "ExceptionType", Operator = GridFilterOperator.In, Value = "TimeoutException" },
            new GridFilter { Field = "OccurrenceCount", Operator = GridFilterOperator.GreaterThan, Value = "5" }
        ]);

        Assert.Equal([23, 21, 24], byTypes.Items.Select(e => e.Id));
        Assert.Equal(3, byTypes.TotalCount);
        Assert.Equal([23, 21], byMessage.Items.Select(e => e.Id));
        Assert.Equal([22, 23], frequent.Items.Select(e => e.Id));
        Assert.Equal(2, frequent.TotalCount);
        Assert.Equal([22, 23, 24], firstSeenRange.Items.Select(e => e.Id));
        Assert.Equal(22, Assert.Single(seenLastHalfHour.Items).Id);
        Assert.Equal(23, Assert.Single(combined.Items).Id);
        Assert.Equal(1, combined.TotalCount);
    }

    [Theory]
    [InlineData("MonitoredAppId")]
    [InlineData("Fingerprint")]
    [InlineData("TopFrame")]
    [InlineData("Message; DROP TABLE AppErrorEvents")]
    public async Task Errors_SortOnAColumnOutsideTheAllowList_IsABadRequest(string sortBy)
    {
        await SeedGridErrorsAsync();

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => GridErrorsAsync(sortBy: sortBy));
    }

    [Fact]
    public async Task Errors_UnknownFilterColumn_UnsupportedOperatorOrInvalidValue_IsABadRequest()
    {
        await SeedGridErrorsAsync();

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => GridErrorsAsync(filters:
            [new GridFilter { Field = "MonitoredAppId", Operator = GridFilterOperator.Equals, Value = "2" }]));
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => GridErrorsAsync(filters:
            [new GridFilter { Field = "Fingerprint", Operator = GridFilterOperator.Equals, Value = "a" }]));
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => GridErrorsAsync(filters:
            [new GridFilter { Field = "OccurrenceCount", Operator = GridFilterOperator.GreaterThan, Value = "many" }]));
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => GridErrorsAsync(filters:
            [new GridFilter { Field = "LastSeenAt", Operator = GridFilterOperator.GreaterThan, Value = "yesterday-ish" }]));
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => GridErrorsAsync(filters:
            [new GridFilter { Field = "OccurrenceCount", Operator = GridFilterOperator.Contains, Value = "4" }]));
    }

    [Fact]
    public async Task Errors_ExceptionTypes_AreEveryDistinctTypeOfTheApp_Alphabetical()
    {
        await SeedGridErrorsAsync();

        var types = await _sut.GetErrorExceptionTypesAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(["IOException", "NullReferenceException", "TimeoutException"], types);
    }
}
