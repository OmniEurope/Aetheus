// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;
using System.Security.Cryptography;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aetheus.Back.IntegrationTests;

public sealed class AppWebAnalyticsRepositoryIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RecordSummaryReplayAndTieredPurge_ExecuteOnPostgres()
    {
        await fixture.ResetAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var organizationId = await db.Organizations.OrderBy(item => item.Id)
            .Select(item => item.Id)
            .FirstAsync(TestContext.Current.CancellationToken);
        var project = new Project { Name = "Analytics integration", OrganizationId = organizationId };
        db.Projects.Add(project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var app = new MonitoredApp
        {
            ProjectId = project.Id,
            Name = "portfolio",
            Enabled = true,
            AnalyticsEnabled = true,
            AnalyticsSiteId = "portfolio-integration",
            AnalyticsPseudonymKeyVersion = 1,
            AnalyticsStorageBudgetBytes = 104_857_600
        };
        db.MonitoredApps.Add(app);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var analyticsEvent = Event(app.Id, now);
        var repository = new AppWebAnalyticsRepository(db);

        var first = await repository.RecordAsync(
            app.Id,
            [analyticsEvent],
            30,
            now,
            TestContext.Current.CancellationToken);
        var replay = await repository.RecordAsync(
            app.Id,
            [analyticsEvent],
            30,
            now,
            TestContext.Current.CancellationToken);
        var summary = await repository.GetSummaryAsync(
            app.Id,
            DateOnly.FromDateTime(now),
            WeekStart(now),
            new DateOnly(now.Year, now.Month, 1),
            DateOnly.FromDateTime(now.AddDays(-29)),
            104_857_600,
            0,
            now,
            TestContext.Current.CancellationToken);
        var purged = await repository.PurgeAsync(
            now.AddMinutes(1),
            now.AddMinutes(1),
            DateOnly.FromDateTime(now).AddDays(1),
            now.AddMinutes(1),
            TestContext.Current.CancellationToken);

        Assert.Equal((1, 0), first);
        Assert.Equal((0, 1), replay);
        Assert.Equal(1, summary.UniqueVisitorsToday);
        Assert.Equal(1, summary.UniqueVisitorsThisWeek);
        Assert.Equal(1, summary.UniqueVisitorsThisMonth);
        Assert.Equal(1, summary.PageViewsThisMonth);
        Assert.Equal(1, purged.Events);
        Assert.Equal(1, purged.Sessions);
        Assert.Equal(3, purged.PeriodIdentities);
        Assert.Equal(3, purged.Aggregates);
        Assert.Equal(1, purged.Pages);
    }

    [Fact]
    public async Task RecordAsync_HundredMixedEventsUsesConstantCommandBudget()
    {
        await fixture.ResetAsync();
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(counter)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var app = await CreateAppAsync(db);
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var events = Enumerable.Range(0, 100)
            .Select(index =>
            {
                var item = Event(app.Id, now.AddSeconds(index));
                return item with
                {
                    Kind = index % 4 == 0 ? "page_view" : "browser_error",
                    Route = $"/route/{index % 5}",
                    ErrorType = index % 4 == 0 ? null : "script_error"
                };
            })
            .ToList();
        counter.Reset();

        var result = await new AppWebAnalyticsRepository(db).RecordAsync(
            app.Id, events, 30, now, TestContext.Current.CancellationToken);

        Assert.Equal((100, 0), result);
        Assert.InRange(counter.Count, 1, 15);
    }

    [Fact]
    public async Task R2007_TrimToAsync_DeletesTheOldestEventsOnPostgres()
    {
        await fixture.ResetAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var app = await CreateAppAsync(db);
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var events = Enumerable.Range(0, 10)
            .Select(index => Event(app.Id, now.AddMinutes(index)) with
            {
                Kind = "browser_error",
                ErrorType = "script_error"
            })
            .ToList();
        var repository = new AppWebAnalyticsRepository(db);
        await repository.RecordAsync(app.Id, events, 30, now, TestContext.Current.CancellationToken);
        var before = await repository.EstimateStorageBytesAsync(app.Id, TestContext.Current.CancellationToken);

        // Three events' worth less than what is stored: the three oldest go, the seven newest stay.
        var trimmed = await repository.TrimToAsync(app.Id, before - 3 * 480, TestContext.Current.CancellationToken);

        Assert.Equal(3, trimmed.Events);
        Assert.Equal(0, trimmed.Sessions);
        var left = await db.AppAnalyticsEvents.AsNoTracking().Where(item => item.MonitoredAppId == app.Id)
            .Select(item => item.OccurredAtUtc).OrderBy(item => item)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(7, left.Count);
        Assert.Equal(now.AddMinutes(3), left[0]);
        Assert.Equal(before - 3 * 480, trimmed.RemainingBytes);
    }

    [Fact]
    public async Task RecordAsync_ConcurrentReplicasAcceptAnEventExactlyOnce()
    {
        await fixture.ResetAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await CreateAppAsync(setup);
        }
        await using var firstDb = new AppDbContext(options);
        await using var secondDb = new AppDbContext(options);
        var appId = await firstDb.MonitoredApps.Select(item => item.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var analyticsEvent = Event(appId, now);

        var results = await Task.WhenAll(
            new AppWebAnalyticsRepository(firstDb).RecordAsync(
                appId, [analyticsEvent], 30, now, TestContext.Current.CancellationToken),
            new AppWebAnalyticsRepository(secondDb).RecordAsync(
                appId, [analyticsEvent], 30, now, TestContext.Current.CancellationToken));

        Assert.Equal(1, results.Sum(item => item.Accepted));
        Assert.Equal(1, results.Sum(item => item.Replayed));
    }

    [Fact]
    public async Task RecordAsync_ConcurrentReplicasMergeDistinctEventsIntoSharedAnalyticsRows()
    {
        await fixture.ResetAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await CreateAppAsync(setup);
        }
        await using var firstDb = new AppDbContext(options);
        await using var secondDb = new AppDbContext(options);
        var appId = await firstDb.MonitoredApps.Select(item => item.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var firstEvent = Event(appId, now);
        var secondEvent = Event(appId, now.AddSeconds(1)) with { EventId = Guid.NewGuid() };

        var results = await Task.WhenAll(
            new AppWebAnalyticsRepository(firstDb).RecordAsync(
                appId, [firstEvent], 30, now, TestContext.Current.CancellationToken),
            new AppWebAnalyticsRepository(secondDb).RecordAsync(
                appId, [secondEvent], 30, now, TestContext.Current.CancellationToken));

        await using var assertionDb = new AppDbContext(options);
        var session = await assertionDb.AppAnalyticsSessions
            .SingleAsync(TestContext.Current.CancellationToken);
        var aggregates = await assertionDb.AppAnalyticsAggregates
            .ToListAsync(TestContext.Current.CancellationToken);
        var page = await assertionDb.AppAnalyticsPageAggregates
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Sum(item => item.Accepted));
        Assert.Equal(0, results.Sum(item => item.Replayed));
        Assert.Equal(2, await assertionDb.AppAnalyticsEvents.CountAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(2, session.PageViewCount);
        Assert.Equal(3, await assertionDb.AppAnalyticsPeriodIdentities.CountAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(3, aggregates.Count);
        Assert.All(aggregates, aggregate =>
        {
            Assert.Equal(2, aggregate.PageViews);
            Assert.Equal(1, aggregate.UniqueVisitors);
            Assert.Equal(1, aggregate.Sessions);
        });
        Assert.Equal(2, page.PageViews);
    }

    [Fact]
    public async Task RecordAsync_OlderEventWithinTimeoutMergesIntoNewerSession()
    {
        await fixture.ResetAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await CreateAppAsync(setup);
        }

        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var newerEvent = Event(0, now.AddMinutes(10));
        var olderEvent = Event(0, now) with { EventId = Guid.NewGuid() };
        await using (var newerDb = new AppDbContext(options))
        {
            var appId = await newerDb.MonitoredApps.Select(item => item.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
            await new AppWebAnalyticsRepository(newerDb).RecordAsync(
                appId, [newerEvent], 30, now, TestContext.Current.CancellationToken);
        }
        await using (var olderDb = new AppDbContext(options))
        {
            var appId = await olderDb.MonitoredApps.Select(item => item.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
            await new AppWebAnalyticsRepository(olderDb).RecordAsync(
                appId, [olderEvent], 30, now, TestContext.Current.CancellationToken);
        }

        await using var assertionDb = new AppDbContext(options);
        var session = await assertionDb.AppAnalyticsSessions
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(now, session.StartedAtUtc);
        Assert.Equal(now.AddMinutes(10), session.LastSeenAtUtc);
        Assert.Equal(2, session.PageViewCount);
    }

    private static async Task<MonitoredApp> CreateAppAsync(AppDbContext db)
    {
        var organizationId = await db.Organizations.OrderBy(item => item.Id)
            .Select(item => item.Id)
            .FirstAsync(TestContext.Current.CancellationToken);
        var project = new Project { Name = $"Analytics {Guid.NewGuid():N}", OrganizationId = organizationId };
        db.Projects.Add(project);
        var app = new MonitoredApp
        {
            Project = project,
            Name = "portfolio",
            Enabled = true,
            AnalyticsEnabled = true,
            AnalyticsSiteId = $"analytics-{Guid.NewGuid():N}",
            AnalyticsPseudonymKeyVersion = 1,
            AnalyticsStorageBudgetBytes = 104_857_600
        };
        db.MonitoredApps.Add(app);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static AppWebAnalyticsIngestEvent Event(int appId, DateTime occurredAtUtc) => new()
    {
        SchemaVersion = 1,
        ApplicationId = appId,
        SiteId = "portfolio-integration",
        EventId = Guid.NewGuid(),
        OccurredAtUtc = occurredAtUtc,
        Kind = "page_view",
        Route = "/projects/{id}",
        DailyPseudonym = Hash("daily"),
        WeeklyPseudonym = Hash("weekly"),
        MonthlyPseudonym = Hash("monthly"),
        SessionPseudonym = Hash("session"),
        KeyVersion = 1
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static DateOnly WeekStart(DateTime instant)
    {
        var day = DateOnly.FromDateTime(instant);
        return day.AddDays(-(((int)instant.DayOfWeek + 6) % 7));
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public void Reset() => Count = 0;
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count++;
            return result;
        }
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Count++;
            return result;
        }
        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Count++;
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
