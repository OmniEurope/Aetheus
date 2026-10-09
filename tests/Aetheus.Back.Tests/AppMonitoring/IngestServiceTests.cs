// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.AppMonitoring.Ingest;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

public class IngestServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly IngestService _service;
    private readonly IngestKeyHasher _hasher;
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IAppTelemetryChangePublisher _telemetryChanges = Substitute.For<IAppTelemetryChangePublisher>();
    private readonly RecordingLogger<IngestKeyRejectionLog> _rejectionLog = new();

    public IngestServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
        var appRepo = new AppMonitoringRepository(_db);
        var metricRepo = new AppMetricRepository(_db);
        _hasher = new IngestKeyHasher(Options.Create(new JwtOptions { SigningKey = "unit-test-signing-key-least-32-bytes!!" }));
        _service = new IngestService(appRepo, metricRepo, new AppLogRepository(_db), new AppErrorRepository(_db),
            _hasher, new MemoryCache(new MemoryCacheOptions()), _notifications, new AppIngestGate(),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero)),
            Substitute.For<ILogger<IngestService>>(), _telemetryChanges,
            new IngestKeyRejectionLog(
                new FakeTimeProvider(new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero)), _rejectionLog));

        _db.MonitoredApps.Add(new MonitoredApp
        {
            Id = 1,
            ProjectId = 1,
            Name = "app",
            IngestKeyHash = _hasher.Hash("secret-key"),
            IngestKeyCreatedAt = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc),
            IngestKeyExpiresAt = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc)
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ResolveAppId_MatchesHashedKey_RejectsUnknown()
    {
        Assert.Equal(1, await _service.ResolveAppIdAsync("secret-key", ct: TestContext.Current.CancellationToken));
        Assert.Null(await _service.ResolveAppIdAsync("wrong-key", ct: TestContext.Current.CancellationToken));
        Assert.Null(await _service.ResolveAppIdAsync("", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAppId_RandomInvalidKeys_BoundsNegativeCache()
    {
        for (var i = 0; i < IngestService.MaxNegativeKeyCacheEntries + 25; i++)
            Assert.Null(await _service.ResolveAppIdAsync(
                $"invalid-{i}",
                ct: TestContext.Current.CancellationToken));

        Assert.InRange(_service.TrackedNegativeKeyCount, 1, IngestService.MaxNegativeKeyCacheEntries);
    }

    [Fact]
    public async Task ResolveAppId_RejectsCurrentKeyAfterNinetyDays()
    {
        var app = _db.MonitoredApps.Single(item => item.Id == 1);
        app.IngestKeyExpiresAt = new DateTime(2026, 1, 10, 11, 59, 59, DateTimeKind.Utc);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await _service.ResolveAppIdAsync(
            "secret-key",
            ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task R2_013_ARefusedKey_IsOneWarning_ThenCounted_EvenFromTheNegativeCache()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await _service.ResolveAppIdAsync("stale-key", ct)); // database miss
        Assert.Null(await _service.ResolveAppIdAsync("stale-key", ct)); // negative-cache hit
        Assert.Equal(1, await _service.ResolveAppIdAsync("secret-key", ct));

        var warning = Assert.Single(_rejectionLog.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(_hasher.Hash("stale-key")[..12], warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R2_013_AMissingKey_IsReportedAsMissing()
    {
        Assert.Null(await _service.ResolveAppIdAsync(" ", TestContext.Current.CancellationToken));

        Assert.Contains(IngestKeyRejectionLog.MissingKey, Assert.Single(_rejectionLog.Entries).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidateKeyCache_EvictsMapping_SoRevokedKeyStopsResolving()
    {
        // Prime the resolver cache (hash -> appId 1).
        Assert.Equal(1, await _service.ResolveAppIdAsync("secret-key", ct: TestContext.Current.CancellationToken));

        // Revoke the key in the store, as AppTelemetryService.RevokeIngestKeyAsync does.
        var app = _db.MonitoredApps.Single(a => a.Id == 1);
        app.IngestKeyHash = null;
        _db.SaveChanges();

        // Without eviction the cached mapping would keep resolving for the whole TTL (the bug).
        Assert.Equal(1, await _service.ResolveAppIdAsync("secret-key", ct: TestContext.Current.CancellationToken));

        // After eviction the revoked key stops resolving immediately.
        _service.InvalidateKeyCache(_hasher.Hash("secret-key"));
        Assert.Null(await _service.ResolveAppIdAsync("secret-key", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestMetrics_PersistsSamples_AndTouchesApp()
    {
        var points = new List<ParsedMetricPoint>
        {
            new("cpu", 12.5, "%", default, null),
            new("mem", 512, "MB", default, "{\"host\":\"a\"}")
        };
        var outcome = await _service.IngestMetricsAsync(1, points, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, outcome.Accepted);
        Assert.Equal(0, outcome.Dropped);
        Assert.Equal(2, await _db.AppMetricSamples.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        var app = await _db.MonitoredApps.AsNoTracking().SingleAsync(a => a.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(app.LastIngestAt);
    }

    [Fact]
    public async Task IngestMetrics_CardinalityCap_DropsExcessNewNames()
    {
        // 201 distinct new metric names, cap is 200 -> exactly one dropped, counted on the app.
        var points = Enumerable.Range(0, IngestService.MaxMetricNamesPerApp + 1)
            .Select(i => new ParsedMetricPoint($"metric_{i}", i, null, default, null))
            .ToList();

        var outcome = await _service.IngestMetricsAsync(1, points, ct: TestContext.Current.CancellationToken);

        Assert.Equal(IngestService.MaxMetricNamesPerApp, outcome.Accepted);
        Assert.Equal(1, outcome.Dropped);
        var app = await _db.MonitoredApps.AsNoTracking().SingleAsync(a => a.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, app.IngestDroppedCount);
    }

    [Fact]
    public async Task IngestMetrics_SustainedRateCap_DropsExcessWithinTheMinute()
    {
        // S-TECH-VOLM: same metric name (no cardinality interference), all timestamped "now" (in window).
        // Push in per-request-capped batches until the per-minute ceiling is crossed: the overflow is
        // dropped honestly and counted, and no more than the ceiling is persisted.
        const int batch = IngestService.MaxPointsPerRequest; // 5000
        var batches = (IngestService.MaxSamplesPerMinutePerApp / batch) + 1; // one batch past the ceiling
        var totalDropped = 0;
        var totalAccepted = 0;
        for (var b = 0; b < batches; b++)
        {
            var points = Enumerable.Range(0, batch)
                .Select(i => new ParsedMetricPoint("cpu", i, "%", default, null))
                .ToList();
            var outcome = await _service.IngestMetricsAsync(1, points, ct: TestContext.Current.CancellationToken);
            totalAccepted += outcome.Accepted;
            totalDropped += outcome.Dropped;
        }

        Assert.Equal(IngestService.MaxSamplesPerMinutePerApp, totalAccepted);
        Assert.Equal(IngestService.MaxSamplesPerMinutePerApp, await _db.AppMetricSamples.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(batches * batch - IngestService.MaxSamplesPerMinutePerApp, totalDropped);
    }

    [Fact]
    public async Task IngestMetrics_OutOfWindowTimestamp_IsDropped()
    {
        // now = 2026-01-10 12:00Z (FakeTimeProvider). A point dated a year in the future would fall outside
        // both the aggregation window and the retention purge -> permanent orphan row. It must be dropped.
        var future = new DateTime(2027, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var ancient = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var outcome = await _service.IngestMetricsAsync(1,
        [
            new ParsedMetricPoint("cpu", 1, "%", future, null),
            new ParsedMetricPoint("cpu", 2, "%", ancient, null)
        ], ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.Accepted);
        Assert.Equal(2, outcome.Dropped);
        Assert.Equal(0, await _db.AppMetricSamples.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestMetrics_ThresholdBreach_StartsTheAiTasksOfTheAppProjectOnly()
    {
        // BACK-003 follow-up: an AI task starts only for an event naming its owner, so the breach
        // carries the project of the monitored app (project 1 here).
        _db.AppMetricThresholds.Add(new AppMetricThreshold
        {
            MonitoredAppId = 1,
            MetricName = "cpu",
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 80,
            Enabled = true
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        object? payload = null;
        await _notifications.SendEventAsync(
            "app.metric.threshold",
            Arg.Do<object>(value => payload = value),
            Arg.Any<CancellationToken>());

        await _service.IngestMetricsAsync(1, [new ParsedMetricPoint("cpu", 90, "%", default, null)], ct: TestContext.Current.CancellationToken);

        Assert.NotNull(payload);
        Assert.Equal(
            ["own"],
            await Aetheus.Back.Tests.AiTasks.AiEventDispatchProbe.StartedDefinitionsAsync(
                "app.metric.threshold", payload, ownProjectId: 1, foreignProjectId: 2,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestMetrics_ThresholdBreach_FiresOnce_ThenRecovers()
    {
        _db.AppMetricThresholds.Add(new AppMetricThreshold
        {
            MonitoredAppId = 1,
            MetricName = "cpu",
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 80,
            Enabled = true
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _service.IngestMetricsAsync(1, [new ParsedMetricPoint("cpu", 90, "%", default, null)], ct: TestContext.Current.CancellationToken);
        await _notifications.Received(1).SendEventAsync("app.metric.threshold", Arg.Any<object>(), Arg.Any<CancellationToken>());
        Assert.True(await _db.AppMetricThresholds.AsNoTracking().Select(t => t.IsBreached).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));

        // Still breached -> no duplicate notification.
        await _service.IngestMetricsAsync(1, [new ParsedMetricPoint("cpu", 95, "%", default, null)], ct: TestContext.Current.CancellationToken);
        await _notifications.Received(1).SendEventAsync("app.metric.threshold", Arg.Any<object>(), Arg.Any<CancellationToken>());

        // Back under threshold -> clears the breach.
        await _service.IngestMetricsAsync(1, [new ParsedMetricPoint("cpu", 40, "%", default, null)], ct: TestContext.Current.CancellationToken);
        Assert.False(await _db.AppMetricThresholds.AsNoTracking().Select(t => t.IsBreached).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Recette R-478: a breach alert still being dispatched does not hold the app's ingest lock, so the
    /// app's next batch is ingested meanwhile; and that batch, which breaches too, does not alert again
    /// because the breach was recorded before the lock was released.
    /// </summary>
    [Fact]
    public async Task R478_ASlowAlertDispatch_DoesNotHoldTheAppsNextBatch_NorAlertTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.AppMetricThresholds.Add(new AppMetricThreshold
        {
            MonitoredAppId = 1,
            MetricName = "cpu",
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 80,
            Enabled = true
        });
        await _db.SaveChangesAsync(ct);
        var dispatching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _notifications.SendEventAsync("app.metric.threshold", Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                dispatching.TrySetResult();
                return release.Task;
            });

        var first = _service.IngestMetricsAsync(1, [new ParsedMetricPoint("cpu", 90, "%", default, null)], ct);
        await dispatching.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        try
        {
            var second = _service.IngestMetricsAsync(1, [new ParsedMetricPoint("cpu", 95, "%", default, null)], ct);
            Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5), ct)));
            Assert.Equal(1, (await second).Accepted);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(1, (await first).Accepted);
        await _notifications.Received(1).SendEventAsync("app.metric.threshold", Arg.Any<object>(), Arg.Any<CancellationToken>());
        Assert.Equal(2, await _db.AppMetricSamples.CountAsync(ct));
    }

    [Fact]
    public async Task R477_ANameSeenForTheFirstTime_IsRecordedOnce_AndPrunedWithItsLastSample()
    {
        var ct = TestContext.Current.CancellationToken;
        await _service.IngestMetricsAsync(1,
            [new ParsedMetricPoint("cpu", 1, "%", default, null), new ParsedMetricPoint("mem", 2, "MiB", default, null)], ct: ct);
        await _service.IngestMetricsAsync(1, [new ParsedMetricPoint("cpu", 3, "%", default, null)], ct: ct);

        Assert.Equal(["cpu", "mem"], await _db.AppMetricNames.AsNoTracking().OrderBy(n => n.Name).Select(n => n.Name).ToListAsync(ct));

        // The retention sweep purged the last samples of "mem": the name goes with them.
        _db.AppMetricSamples.RemoveRange(_db.AppMetricSamples.Where(sample => sample.MetricName == "mem"));
        await _db.SaveChangesAsync(ct);
        var pruned = await new AppMetricRepository(_db).PruneMetricNamesAsync(ct);

        Assert.Equal(1, pruned);
        Assert.Equal(["cpu"], await _db.AppMetricNames.AsNoTracking().Select(n => n.Name).ToListAsync(ct));
    }

    [Fact]
    public async Task IngestLogs_StoredRows_PushTheChangeToTheOpenViews()
    {
        var outcome = await _service.IngestLogsAsync(1,
            [new ParsedLogRecord(default, 9, "Information", "started", null)],
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Accepted);
        Assert.Equal(1, await _db.AppLogEntries.CountAsync(TestContext.Current.CancellationToken));
        await _telemetryChanges.Received(1).PublishAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R491_AnErrorLoggedWithAnException_OpensAnErrorGroup()
    {
        var record = new ParsedLogRecord(
            default, 17, "Error", "Error during app telemetry retention sweep", null,
            ExceptionType: "Microsoft.EntityFrameworkCore.DbUpdateException",
            Source: "Aetheus.Back.Services.AppTelemetryRetentionService");

        // The same failure twice, a warning with an exception, and an error without one.
        await _service.IngestLogsAsync(1,
            [
                record, record,
                new ParsedLogRecord(default, 13, "Warning", "retrying", null, ExceptionType: "System.TimeoutException", Source: "X"),
                new ParsedLogRecord(default, 17, "Error", "plain error line", null)
            ],
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, await _db.AppLogEntries.CountAsync(TestContext.Current.CancellationToken));
        var group = await _db.AppErrorEvents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Microsoft.EntityFrameworkCore.DbUpdateException", group.ExceptionType);
        Assert.Equal("Error during app telemetry retention sweep", group.Message);
        Assert.Equal("Aetheus.Back.Services.AppTelemetryRetentionService", group.TopFrame);
        Assert.Equal(2, group.OccurrenceCount);
    }

    [Fact]
    public async Task IngestLogs_NothingStored_PushesNothing()
    {
        // Out of the ingest window: dropped and counted, no row, so no view has anything new to show.
        var outcome = await _service.IngestLogsAsync(1,
            [new ParsedLogRecord(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 9, null, "old", null)],
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.Accepted);
        await _telemetryChanges.DidNotReceive().PublishAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestErrors_StoredGroups_PushTheChange_AndMetricsDoNot()
    {
        await _service.IngestMetricsAsync(1, [new ParsedMetricPoint("cpu", 1, "%", default, null)],
            ct: TestContext.Current.CancellationToken);
        await _telemetryChanges.DidNotReceive().PublishAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        await _service.IngestErrorsAsync(1,
            [new ParsedError("InvalidOperationException", "boom", "at Foo.Bar()", default)],
            ct: TestContext.Current.CancellationToken);

        await _telemetryChanges.Received(1).PublishAsync(1, Arg.Any<CancellationToken>());
    }
}
