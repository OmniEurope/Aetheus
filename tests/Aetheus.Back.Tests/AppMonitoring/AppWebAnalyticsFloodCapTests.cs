// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.AppMonitoring.Ingest;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>Reject audience batches above the rolling-hour flood cap without deleting stored rows.
/// Database-backed accepted volumes include ingestion across both blue-green colours.</summary>
public sealed class AppWebAnalyticsFloodCapTests : IDisposable
{
    private const int StoredEvents = 10;
    private readonly AppDbContext _db;
    private readonly IAppTelemetryChangePublisher _telemetryChanges = Substitute.For<IAppTelemetryChangePublisher>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public AppWebAnalyticsFloodCapTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        _db.MonitoredApps.Add(new MonitoredApp
        {
            Id = 1,
            ProjectId = 1,
            Name = "portfolio",
            Enabled = true,
            AnalyticsEnabled = true,
            AnalyticsPseudonymKeyVersion = 1,
            AnalyticsPseudonymKeyCreatedAt = Now,
            // Ten stored events weigh 4 800 bytes: the budget is reached, a roll removes the oldest one.
            AnalyticsStorageBudgetBytes = StoredEvents * 480
        });
        for (var minute = 0; minute < StoredEvents; minute++)
        {
            _db.AppAnalyticsEvents.Add(new AppAnalyticsEvent
            {
                MonitoredAppId = 1,
                EventId = Guid.NewGuid(),
                OccurredAtUtc = Now.AddMinutes(-60 + minute),
                Kind = "browser_performance",
                Route = "/projects/{id}",
                DurationMs = 100,
                SessionPseudonym = "seed",
                KeyVersion = 1
            });
        }
        _db.SaveChanges();
    }

    private AppWebAnalyticsService Service(int? rollingEventsPerHour = null)
    {
        var settings = new Dictionary<string, string?>();
        if (rollingEventsPerHour is { } cap)
            settings["AppMonitoring:Analytics:RollingEventsPerHour"] = cap.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new AppWebAnalyticsService(
            new AppWebAnalyticsRepository(_db),
            new AppMonitoringRepository(_db),
            Substitute.For<INotificationService>(),
            new AppIngestGate(),
            _telemetryChanges,
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            _time);
    }

    private void SeedAcceptedVolume(int count, DateTime receivedAtUtc)
    {
        _db.AppAnalyticsIngestVolumes.Add(new AppAnalyticsIngestVolume
        {
            MonitoredAppId = 1,
            ReceivedAtUtc = receivedAtUtc,
            Count = count
        });
        _db.SaveChanges();
    }

    private Task<WebAnalyticsIngestOutcome> IngestOnePageViewAsync(AppWebAnalyticsService service) =>
        service.IngestAsync(1,
        [
            new AppWebAnalyticsIngestEvent
            {
                SchemaVersion = 1,
                ApplicationId = 1,
                SiteId = "portfolio",
                EventId = Guid.NewGuid(),
                OccurredAtUtc = Now,
                Kind = "page_view",
                Route = "/projects/{id}",
                DailyPseudonym = new string('a', 64),
                WeeklyPseudonym = new string('b', 64),
                MonthlyPseudonym = new string('c', 64),
                SessionPseudonym = new string('e', 64),
                KeyVersion = 1
            }
        ], TestContext.Current.CancellationToken);

    [Fact]
    public async Task AtTheBudget_UnderTheRollingCap_RollsTheOldestEventAndAcceptsTheBatch()
    {
        // 4 999 events this hour plus the batch reach the default cap of 5 000 without passing it; the
        // 20 000 of two hours ago are out of the rolling hour.
        SeedAcceptedVolume(AppMonitoringDefaults.DefaultAnalyticsRollingEventsPerHour - 1, Now.AddMinutes(-30));
        SeedAcceptedVolume(20_000, Now.AddHours(-2));
        var oldest = Now.AddMinutes(-60);

        var outcome = await IngestOnePageViewAsync(Service());

        Assert.Equal(1, outcome.Accepted);
        Assert.Equal(0, outcome.Rejected);
        var stored = await _db.AppAnalyticsEvents.Select(item => item.OccurredAtUtc)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StoredEvents, stored.Count);
        Assert.DoesNotContain(oldest, stored);
        Assert.Contains(Now, stored);
        Assert.Empty(_db.AppAnalyticsRejections);
        // The accepted batch saved its own volume, which the next batches count.
        Assert.Equal(1, await _db.AppAnalyticsIngestVolumes
            .Where(item => item.ReceivedAtUtc == Now)
            .SumAsync(item => item.Count, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AtTheBudget_OverTheRollingCap_RefusesTheBatchAsAFloodAndDeletesNothing()
    {
        // Saved by the other colour: the cap holds across both.
        SeedAcceptedVolume(AppMonitoringDefaults.DefaultAnalyticsRollingEventsPerHour, Now.AddMinutes(-59));
        var before = await _db.AppAnalyticsEvents.Select(item => item.Id)
            .OrderBy(id => id).ToListAsync(TestContext.Current.CancellationToken);

        var outcome = await IngestOnePageViewAsync(Service());

        Assert.Equal(0, outcome.Accepted);
        Assert.Equal(1, outcome.Rejected);
        Assert.Equal(before, await _db.AppAnalyticsEvents.Select(item => item.Id)
            .OrderBy(id => id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_db.AppAnalyticsSessions);
        var rejection = Assert.Single(_db.AppAnalyticsRejections);
        Assert.Equal("flood", rejection.ReasonCode);
        Assert.Equal(1, rejection.Count);
        var app = await _db.MonitoredApps.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, app.AnalyticsRejectedCount);
        Assert.Single(_db.AppAnalyticsIngestVolumes);
        await _telemetryChanges.DidNotReceive().PublishAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BelowTheBudget_TheCapDoesNotApply()
    {
        var app = await _db.MonitoredApps.SingleAsync(TestContext.Current.CancellationToken);
        app.AnalyticsStorageBudgetBytes = 104_857_600;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        SeedAcceptedVolume(50_000, Now.AddMinutes(-10));

        var outcome = await IngestOnePageViewAsync(Service());

        Assert.Equal(1, outcome.Accepted);
        Assert.Equal(StoredEvents + 1, await _db.AppAnalyticsEvents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_db.AppAnalyticsRejections);
    }

    [Fact]
    public async Task TheCap_IsOverriddenByConfiguration()
    {
        SeedAcceptedVolume(150, Now.AddMinutes(-5));

        var outcome = await IngestOnePageViewAsync(Service(rollingEventsPerHour: 150));

        Assert.Equal(0, outcome.Accepted);
        Assert.Equal("flood", Assert.Single(_db.AppAnalyticsRejections).ReasonCode);
        Assert.Equal(StoredEvents, await _db.AppAnalyticsEvents.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Purge_RemovesAcceptedVolumesWithTheRejections()
    {
        SeedAcceptedVolume(3, Now.AddDays(-8));
        SeedAcceptedVolume(4, Now.AddMinutes(-1));

        var purged = await new AppWebAnalyticsRepository(_db).PurgeAsync(
            Now.AddDays(-30), Now.AddDays(-90), DateOnly.FromDateTime(Now).AddMonths(-25), Now.AddDays(-7),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, purged.IngestVolumes);
        Assert.Equal(4, Assert.Single(_db.AppAnalyticsIngestVolumes).Count);
    }

    public void Dispose() => _db.Dispose();
}
