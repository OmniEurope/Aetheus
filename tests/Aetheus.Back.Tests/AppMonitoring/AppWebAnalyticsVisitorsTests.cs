// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>Recette R-22: each daily visitor keeps its first visit time and entry page, never an IP.</summary>
public sealed class AppWebAnalyticsVisitorsTests : IDisposable
{
    private const string Address = "203.0.113.24";
    private const string Secret = "unit-test-secret-never-sent-to-browser";

    private readonly AppDbContext _db;
    private readonly AppWebAnalyticsRepository _repository;
    private readonly AppWebAnalyticsService _service;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero));

    public AppWebAnalyticsVisitorsTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        _db.MonitoredApps.Add(App());
        _db.SaveChanges();
        _repository = new AppWebAnalyticsRepository(_db);
        _service = new AppWebAnalyticsService(
            _repository,
            new AppMonitoringRepository(_db),
            Substitute.For<INotificationService>(),
            new AppIngestGate(),
            Substitute.For<IAppTelemetryChangePublisher>(),
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().Build(),
            _time);
    }

    [Fact]
    public async Task FirstVisit_IsSetOnceAndNotOverwrittenByLaterHitsTheSameDay()
    {
        var firstVisit = _time.GetUtcNow().UtcDateTime;
        await Ingest("/", address: Address);
        _time.Advance(TimeSpan.FromHours(3));
        await Ingest("/pricing", address: Address);
        await Ingest("/pricing", address: Address, kind: "heartbeat");

        var visitors = await _service.GetVisitorsAsync(1, 30, TestContext.Current.CancellationToken);

        var visitor = Assert.Single(visitors!);
        Assert.Equal(firstVisit, visitor.FirstSeenAtUtc);
        Assert.Equal(DateTimeKind.Utc, visitor.FirstSeenAtUtc.Kind);
    }

    [Fact]
    public async Task EntryPage_IsTheFirstPageViewAndNotTheHeartbeatRouteNorLaterPages()
    {
        await Ingest("/heartbeat-only-route", address: Address, kind: "heartbeat");
        Assert.Null(Assert.Single((await _service.GetVisitorsAsync(1, 30, TestContext.Current.CancellationToken))!).EntryRoute);

        _time.Advance(TimeSpan.FromMinutes(1));
        await Ingest("/Orders/123", address: Address);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Ingest("/settings", address: Address);

        var visitor = Assert.Single((await _service.GetVisitorsAsync(1, 30, TestContext.Current.CancellationToken))!);
        Assert.Equal("/orders/{id}", visitor.EntryRoute);
    }

    [Fact]
    public async Task EntryPage_IsKeptPerVisitorAndListedNewestFirstVisitFirst()
    {
        await Ingest("/a", address: "203.0.113.1");
        _time.Advance(TimeSpan.FromMinutes(2));
        await Ingest("/b", address: "198.51.100.7");

        var visitors = await _service.GetVisitorsAsync(1, 30, TestContext.Current.CancellationToken);

        Assert.Equal(["/b", "/a"], visitors!.Select(item => item.EntryRoute));
    }

    [Fact]
    public async Task Visitors_OnlyCoverTheRequestedDays()
    {
        await Ingest("/old", address: Address);
        _time.Advance(TimeSpan.FromDays(3));
        await Ingest("/recent", address: Address);

        var lastDay = await _service.GetVisitorsAsync(1, 1, TestContext.Current.CancellationToken);
        var lastWeek = await _service.GetVisitorsAsync(1, 7, TestContext.Current.CancellationToken);

        Assert.Equal(["/recent"], lastDay!.Select(item => item.EntryRoute));
        Assert.Equal(2, lastWeek!.Count);
    }

    [Fact]
    public async Task Visitors_UnknownApp_IsNull()
    {
        Assert.Null(await _service.GetVisitorsAsync(99, 30, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoredRowsAndDto_ContainNoIpAddressNorUserAgent()
    {
        await Ingest("/", address: Address);

        var rows = await _db.AppAnalyticsPeriodIdentities.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        var visitors = await _service.GetVisitorsAsync(1, 30, TestContext.Current.CancellationToken);
        var stored = string.Join(
            '\n',
            rows.Select(row => $"{row.Pseudonym}|{row.FirstSeenAtUtc:O}|{row.LastSeenAtUtc:O}|{row.EntryRoute}|{row.KeyVersion}"))
            + JsonSerializer.Serialize(visitors);

        Assert.NotEmpty(rows);
        Assert.Contains("/", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Address, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113", stored, StringComparison.Ordinal);
        var dto = Assert.Single(visitors!);
        Assert.Equal(8, dto.Visitor.Length);
        Assert.Equal(
            ["EntryRoute", "FirstSeenAtUtc", "Visitor"],
            typeof(AppWebAnalyticsVisitorDto).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Purge_RemovesTheEntryPageAndFirstVisitWithTheVisitorRow()
    {
        await Ingest("/", address: Address);
        _time.Advance(TimeSpan.FromDays(100));
        var now = _time.GetUtcNow().UtcDateTime;

        var purged = await _repository.PurgeAsync(
            now.AddDays(-30), now.AddDays(-90), DateOnly.FromDateTime(now).AddDays(-400), now.AddDays(-7),
            TestContext.Current.CancellationToken);

        Assert.True(purged.PeriodIdentities > 0);
        Assert.Empty((await _service.GetVisitorsAsync(1, 90, TestContext.Current.CancellationToken))!);
        Assert.Empty(await _db.AppAnalyticsPeriodIdentities.Where(row => row.EntryRoute != null).ToListAsync(TestContext.Current.CancellationToken));
    }

    private async Task Ingest(string route, string address, string kind = "page_view")
    {
        var app = await _db.MonitoredApps.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var prepared = PublicWebAnalyticsPseudonymizer.Create(
            app,
            Secret,
            new PublicWebAnalyticsEventRequest
            {
                SchemaVersion = 1,
                EventId = Guid.NewGuid(),
                OccurredAtUtc = _time.GetUtcNow().UtcDateTime,
                Kind = kind,
                Route = route
            },
            address);
        var outcome = await _service.IngestAsync(1, [prepared], TestContext.Current.CancellationToken);
        Assert.Equal(1, outcome.Accepted);
    }

    private MonitoredApp App() => new()
    {
        Id = 1,
        ProjectId = 1,
        Name = "portfolio",
        Enabled = true,
        AnalyticsEnabled = true,
        AnalyticsSiteId = "portfolio",
        AnalyticsPseudonymKeyVersion = 1,
        AnalyticsPseudonymKeyCreatedAt = _time.GetUtcNow().UtcDateTime,
        AnalyticsStorageBudgetBytes = 104_857_600
    };

    public void Dispose() => _db.Dispose();
}
