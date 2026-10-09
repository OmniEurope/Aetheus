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

public sealed class AppWebAnalyticsServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AppWebAnalyticsService _service;
    private readonly IAppTelemetryChangePublisher _telemetryChanges = Substitute.For<IAppTelemetryChangePublisher>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly FakeTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero));

    public AppWebAnalyticsServiceTests()
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
            AnalyticsPseudonymKeyCreatedAt = _time.GetUtcNow().UtcDateTime,
            AnalyticsStorageBudgetBytes = 104_857_600
        });
        _db.SaveChanges();
        _service = new AppWebAnalyticsService(
            new AppWebAnalyticsRepository(_db),
            new AppMonitoringRepository(_db),
            _notifications,
            new AppIngestGate(),
            _telemetryChanges,
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().Build(),
            _time);
    }

    [Fact]
    public async Task Ingest_DeduplicatesPeriodsWithoutSummingDailyUniques()
    {
        var first = Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a");
        var second = Event(Guid.NewGuid(), "b", "week-a", "month-a", "session-b");

        var outcome = await _service.IngestAsync(1, [first, second], TestContext.Current.CancellationToken);
        var summary = await _service.GetSummaryAsync(1, 30, TestContext.Current.CancellationToken);

        Assert.Equal(2, outcome.Accepted);
        Assert.NotNull(summary);
        Assert.Equal(2, summary.UniqueVisitorsToday);
        Assert.Equal(1, summary.UniqueVisitorsThisWeek);
        Assert.Equal(1, summary.UniqueVisitorsThisMonth);
        Assert.Equal(2, summary.SessionsThisMonth);
        Assert.Equal(2, summary.PageViewsThisMonth);
    }

    [Fact]
    public async Task Ingest_ReplayIsRejectedWithoutChangingCounts()
    {
        var analyticsEvent = Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a");

        await _service.IngestAsync(1, [analyticsEvent], TestContext.Current.CancellationToken);
        var replay = await _service.IngestAsync(1, [analyticsEvent], TestContext.Current.CancellationToken);
        var summary = await _service.GetSummaryAsync(1, 30, TestContext.Current.CancellationToken);

        Assert.Equal(1, replay.Replayed);
        Assert.Equal(1, summary!.PageViewsThisMonth);
        Assert.Equal(1, summary.RejectedEvents);
    }

    [Fact]
    public async Task Ingest_SplitsAuthenticatedSessionAfterThirtyMinutesAndMarksReturn()
    {
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a") with
            {
                AuthenticatedPseudonym = Hash("account-a")
            }],
            TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(31));
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a") with
            {
                OccurredAtUtc = _time.GetUtcNow().UtcDateTime,
                AuthenticatedPseudonym = Hash("account-a")
            }],
            TestContext.Current.CancellationToken);
        var summary = await _service.GetSummaryAsync(1, 30, TestContext.Current.CancellationToken);

        Assert.Equal(2, summary!.SessionsThisMonth);
        Assert.Equal(1, summary.ReturningVisitorsThisMonth);
    }

    [Fact]
    public async Task Ingest_AnonymousRepeatDoesNotCreateReturningVisitorProfile()
    {
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a")],
            TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(31));
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a") with
            {
                OccurredAtUtc = _time.GetUtcNow().UtcDateTime
            }],
            TestContext.Current.CancellationToken);

        var summary = await _service.GetSummaryAsync(
            1,
            30,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, summary!.SessionsThisMonth);
        Assert.Equal(0, summary.ReturningVisitorsThisMonth);
    }

    [Fact]
    public async Task Ingest_RejectsHistoricalKeyWhenRotationTimestampIsUnavailable()
    {
        var app = await _db.MonitoredApps.SingleAsync(TestContext.Current.CancellationToken);
        app.AnalyticsPseudonymKeyVersion = 2;
        app.AnalyticsPseudonymKeyCreatedAt = null;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var historical = await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a")],
            TestContext.Current.CancellationToken);
        var current = await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "b", "week-b", "month-b", "session-b") with { KeyVersion = 2 }],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, historical.Rejected);
        Assert.Equal(0, historical.Accepted);
        Assert.Equal(1, current.Accepted);
    }

    [Fact]
    public async Task Ingest_StoresOptInBrowserSignalsWithoutInflatingAudienceStatistics()
    {
        var browserPerformance = Event(
            Guid.NewGuid(),
            "a",
            "week-a",
            "month-a",
            "session-a") with
        {
            Kind = "browser_performance",
            DurationMs = 124
        };
        var browserError = Event(
            Guid.NewGuid(),
            "a",
            "week-a",
            "month-a",
            "session-a") with
        {
            Kind = "browser_error",
            ErrorType = "script_error"
        };

        var outcome = await _service.IngestAsync(
            1,
            [browserPerformance, browserError],
            TestContext.Current.CancellationToken);
        var summary = await _service.GetSummaryAsync(1, 30, TestContext.Current.CancellationToken);
        var stored = await _db.AppAnalyticsEvents
            .OrderBy(item => item.Kind)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, outcome.Accepted);
        Assert.Equal(0, summary!.PageViewsThisMonth);
        Assert.Equal(0, summary.UniqueVisitorsThisMonth);
        Assert.Equal(0, summary.SessionsThisMonth);
        Assert.Equal(1, summary.BrowserPerformanceSamplesLast30Days);
        Assert.Equal(124, summary.AverageBrowserNavigationDurationMs);
        Assert.Equal(124, summary.P95BrowserNavigationDurationMs);
        Assert.Equal(1, summary.BrowserErrorsLast30Days);
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, item => item.DurationMs == 124 && item.ErrorType is null);
        Assert.Contains(stored, item => item.ErrorType == "script_error" && item.DurationMs is null);
    }

    [Fact]
    public async Task Ingest_RejectsBrowserErrorDetailsOutsideFixedAllowList()
    {
        var unsafeError = Event(
            Guid.NewGuid(),
            "a",
            "week-a",
            "month-a",
            "session-a") with
        {
            Kind = "browser_error",
            ErrorType = "email_alice_example_com"
        };

        var outcome = await _service.IngestAsync(
            1,
            [unsafeError],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.Accepted);
        Assert.Equal(1, outcome.Rejected);
        Assert.Empty(_db.AppAnalyticsEvents);
    }

    [Fact]
    public async Task R2007_RejectionRows_DoNotCountInTheStorageEstimate()
    {
        _db.AppAnalyticsRejections.Add(new AppAnalyticsRejection
        {
            MonitoredAppId = 1,
            OccurredAtUtc = _time.GetUtcNow().UtcDateTime.AddMinutes(-1),
            ReasonCode = "policy",
            Count = 1
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await new AppWebAnalyticsRepository(_db)
            .EstimateStorageBytesAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task R2007_Ingest_AtTheBudget_RollsTheOldestEventsOffAndAcceptsTheBatch()
    {
        var start = _time.GetUtcNow().UtcDateTime;
        for (var minute = 0; minute < 10; minute++)
        {
            _db.AppAnalyticsEvents.Add(StoredEvent(start.AddMinutes(-60 + minute)));
        }
        var app = await _db.MonitoredApps.SingleAsync(TestContext.Current.CancellationToken);
        // Ten stored events weigh 4 800 bytes: a 4 800-byte budget is reached, so the roll brings the
        // app back to 90 % (4 320 bytes) by removing the single oldest event.
        app.AnalyticsStorageBudgetBytes = 10 * 480;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var oldest = start.AddMinutes(-60);

        var outcome = await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a")],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Accepted);
        Assert.Equal(0, outcome.Rejected);
        var stored = await _db.AppAnalyticsEvents.Select(item => item.OccurredAtUtc)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(10, stored.Count);
        Assert.DoesNotContain(oldest, stored);
        Assert.Contains(start, stored);
        Assert.Empty(_db.AppAnalyticsRejections);
        Assert.Equal(95, app.AnalyticsQuotaAlertLevel);
        await _telemetryChanges.Received(1).PublishAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QuotaAlert_StartsTheAiTasksOfTheAppProjectOnly()
    {
        // BACK-003 follow-up: an AI task starts only for an event naming its owner, so the quota alert
        // carries the project of the monitored app (project 1 here). Same budget as the roll case
        // above: the batch reaches it, which raises the 95 % alert.
        var start = _time.GetUtcNow().UtcDateTime;
        for (var minute = 0; minute < 10; minute++)
            _db.AppAnalyticsEvents.Add(StoredEvent(start.AddMinutes(-60 + minute)));
        var app = await _db.MonitoredApps.SingleAsync(TestContext.Current.CancellationToken);
        app.AnalyticsStorageBudgetBytes = 10 * 480;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        object? payload = null;
        await _notifications.SendEventAsync(
            "app.analytics.quota", Arg.Do<object>(value => payload = value), Arg.Any<CancellationToken>());

        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a")],
            TestContext.Current.CancellationToken);

        Assert.NotNull(payload);
        Assert.Equal(
            ["own"],
            await Aetheus.Back.Tests.AiTasks.AiEventDispatchProbe.StartedDefinitionsAsync(
                "app.analytics.quota", payload, ownProjectId: 1, foreignProjectId: 2,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task R2007_RollStorage_BelowTheBudget_RemovesNothing()
    {
        _db.AppAnalyticsEvents.Add(StoredEvent(_time.GetUtcNow().UtcDateTime.AddMinutes(-1)));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var removed = await _service.RollStorageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.Single(_db.AppAnalyticsEvents);
    }

    [Fact]
    public async Task R2007_RollStorage_OverTheBudget_RemovesEventsThenSessionsOldestFirst()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _db.AppAnalyticsEvents.Add(StoredEvent(now.AddMinutes(-5)));
        _db.AppAnalyticsSessions.AddRange(
            new AppAnalyticsSession { MonitoredAppId = 1, SessionPseudonym = "old", StartedAtUtc = now.AddHours(-3), LastSeenAtUtc = now.AddHours(-3), KeyVersion = 1 },
            new AppAnalyticsSession { MonitoredAppId = 1, SessionPseudonym = "new", StartedAtUtc = now.AddMinutes(-2), LastSeenAtUtc = now.AddMinutes(-2), KeyVersion = 1 });
        var app = await _db.MonitoredApps.SingleAsync(TestContext.Current.CancellationToken);
        // 480 + 2 x 320 = 1 120 bytes against a 600-byte budget, 540 bytes the target: the event goes
        // first (640 left), still too much, so the oldest session goes too (320 left).
        app.AnalyticsStorageBudgetBytes = 600;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var removed = await _service.RollStorageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, removed);
        Assert.Empty(_db.AppAnalyticsEvents);
        var session = await _db.AppAnalyticsSessions.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("new", session.SessionPseudonym);
    }

    [Fact]
    public async Task R2007_Heartbeat_IsNotStoredAsAnEventRow()
    {
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a") with { Kind = "heartbeat" }],
            TestContext.Current.CancellationToken);

        Assert.Empty(_db.AppAnalyticsEvents);
        Assert.Single(_db.AppAnalyticsSessions);
    }

    private static AppAnalyticsEvent StoredEvent(DateTime occurredAtUtc) => new()
    {
        MonitoredAppId = 1,
        EventId = Guid.NewGuid(),
        OccurredAtUtc = occurredAtUtc,
        Kind = "browser_performance",
        Route = "/projects/{id}",
        DurationMs = 100,
        SessionPseudonym = "seed",
        KeyVersion = 1
    };

    [Fact]
    public async Task Ingest_StoredEvents_PushTheTelemetryChangeOnce()
    {
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a"), Event(Guid.NewGuid(), "b", "week-a", "month-a", "session-b")],
            TestContext.Current.CancellationToken);

        await _telemetryChanges.Received(1).PublishAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_PublicEventWithAuthenticatedUserId_IsStoredAsAuthenticated()
    {
        var app = await _db.MonitoredApps.SingleAsync(TestContext.Current.CancellationToken);
        var request = new PublicWebAnalyticsEventRequest
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            OccurredAtUtc = _time.GetUtcNow().UtcDateTime,
            Kind = "page_view",
            Route = "/account",
            AuthenticatedUserId = "internal-user-42"
        };
        var prepared = PublicWebAnalyticsPseudonymizer.Create(app, "public-controller-path-secret", request, "203.0.113.55");

        var outcome = await _service.IngestAsync(1, [prepared], TestContext.Current.CancellationToken);
        var stored = await _db.AppAnalyticsEvents.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Accepted);
        Assert.True(stored.Authenticated);
        Assert.NotNull(prepared.AuthenticatedPseudonym);
        Assert.DoesNotContain("internal-user-42", prepared.AuthenticatedPseudonym, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ingest_Heartbeat_RefreshesSessionLastSeenWithoutCountingAsPageView()
    {
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a")],
            TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(4));
        var heartbeatTime = _time.GetUtcNow().UtcDateTime;
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a") with
            {
                Kind = "heartbeat",
                OccurredAtUtc = heartbeatTime
            }],
            TestContext.Current.CancellationToken);

        var session = await _db.AppAnalyticsSessions.SingleAsync(TestContext.Current.CancellationToken);
        var summary = await _service.GetSummaryAsync(1, 30, TestContext.Current.CancellationToken);

        Assert.Equal(heartbeatTime, session.LastSeenAtUtc);
        Assert.Equal(1, session.PageViewCount);
        Assert.Equal(1, summary!.PageViewsThisMonth);
    }

    [Fact]
    public async Task Ingest_AnonymousThenSignedInSameMonth_CountsOneAuthenticatedVisitor()
    {
        // Recette R-351: the first page view of a visit is sent before the app knows the visitor is
        // signed in (and /login is anonymous by nature), so the network-keyed monthly identity already
        // exists when the signed-in events arrive. They share that identity; only their
        // AuthenticatedPseudonym differs, and it must still be counted once.
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a")],
            TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a") with
            {
                OccurredAtUtc = _time.GetUtcNow().UtcDateTime,
                AuthenticatedPseudonym = Hash("signed-in-a")
            }],
            TestContext.Current.CancellationToken);

        var afterFirstSignedIn = await _service.GetSummaryAsync(1, 30, TestContext.Current.CancellationToken);
        Assert.Equal(1, afterFirstSignedIn!.AuthenticatedUniqueThisMonth);

        _time.Advance(TimeSpan.FromMinutes(1));
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a") with
            {
                OccurredAtUtc = _time.GetUtcNow().UtcDateTime,
                AuthenticatedPseudonym = Hash("signed-in-a")
            }],
            TestContext.Current.CancellationToken);

        var summary = await _service.GetSummaryAsync(1, 30, TestContext.Current.CancellationToken);
        Assert.Equal(1, summary!.AuthenticatedUniqueThisMonth);
        // The authenticated identities feed no other counter.
        Assert.Equal(1, summary.UniqueVisitorsThisMonth);
        Assert.Equal(1, summary.UniqueVisitorsThisWeek);
        Assert.Equal(1, summary.UniqueVisitorsToday);
        Assert.Equal(1, summary.SessionsThisMonth);
        Assert.Equal(3, summary.PageViewsThisMonth);
        Assert.Equal(
            3,
            await _db.AppAnalyticsAggregates.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Purge_RemovesAuthenticatedPeriodIdentitiesLikeTheOthers()
    {
        await _service.IngestAsync(
            1,
            [Event(Guid.NewGuid(), "a", "week-a", "month-a", "session-a") with
            {
                AuthenticatedPseudonym = Hash("signed-in-a")
            }],
            TestContext.Current.CancellationToken);
        Assert.Contains(
            await _db.AppAnalyticsPeriodIdentities.ToListAsync(TestContext.Current.CancellationToken),
            item => item.PeriodKind == AnalyticsPeriodKind.AuthenticatedMonth);

        var cutoff = _time.GetUtcNow().UtcDateTime.AddMinutes(1);
        var purged = await new AppWebAnalyticsRepository(_db).PurgeAsync(
            cutoff, cutoff, DateOnly.FromDateTime(cutoff).AddDays(1), cutoff,
            TestContext.Current.CancellationToken);

        Assert.Equal(6, purged.PeriodIdentities);
        Assert.Empty(await _db.AppAnalyticsPeriodIdentities.ToListAsync(TestContext.Current.CancellationToken));
    }

    public void Dispose() => _db.Dispose();

    private AppWebAnalyticsIngestEvent Event(
        Guid eventId,
        string daily,
        string weekly,
        string monthly,
        string session) => new()
        {
            SchemaVersion = 1,
            ApplicationId = 1,
            SiteId = "portfolio",
            EventId = eventId,
            OccurredAtUtc = _time.GetUtcNow().UtcDateTime,
            Kind = "page_view",
            Route = "/projects/{id}",
            DailyPseudonym = Hash(daily),
            WeeklyPseudonym = Hash(weekly),
            MonthlyPseudonym = Hash(monthly),
            SessionPseudonym = Hash(session),
            KeyVersion = 1
        };

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
