// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Reproduces competing rollup inserts on PostgreSQL by committing another writer between
/// the sweep's read and write, verifying conflict tolerance.</summary>
[Collection(PostgresCollection.Name)]
public sealed class HourlyRollupConcurrencyIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static readonly DateTime Hour = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task R462_AMetricRollupAnotherSweepWroteFirst_IsKept_AndTheMissingSeriesStillGoIn()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var appId = await SeedAppAsync(ct);
        await using (var seed = NewContext())
        {
            seed.AppMetricSamples.AddRange(
                new AppMetricSample { MonitoredAppId = appId, MetricName = "cpu", AttributesJson = "{\"k\":\"a\"}", Timestamp = Hour.AddMinutes(5), Value = 2 },
                new AppMetricSample { MonitoredAppId = appId, MetricName = "cpu", AttributesJson = "{\"k\":\"a\"}", Timestamp = Hour.AddMinutes(9), Value = 4 },
                new AppMetricSample { MonitoredAppId = appId, MetricName = "mem", AttributesJson = "{\"k\":\"b\"}", Timestamp = Hour.AddMinutes(7), Value = 10 });
            await seed.SaveChangesAsync(ct);
        }

        var otherColour = new CommitBeforeSave<AppMetricHourly>(async token =>
        {
            await using var other = NewContext();
            other.AppMetricHourly.Add(new AppMetricHourly
            {
                MonitoredAppId = appId,
                MetricName = "cpu",
                AttributesJson = "{\"k\":\"a\"}",
                HourUtc = Hour,
                SampleCount = 99,
                AvgValue = 99
            });
            await other.SaveChangesAsync(token);
        });
        await using (var db = NewContext(otherColour))
        {
            var written = await new AppMetricRepository(db).AggregateHourAsync(Hour, ct);
            Assert.True(otherColour.Fired);
            Assert.Equal(1, written);
        }

        await using var read = NewContext();
        var rows = await read.AppMetricHourly.AsNoTracking()
            .Where(h => h.MonitoredAppId == appId && h.HourUtc == Hour)
            .OrderBy(h => h.MetricName)
            .Select(h => new { h.MetricName, h.SampleCount, h.AvgValue })
            .ToListAsync(ct);
        Assert.Equal(new[] { new { MetricName = "cpu", SampleCount = 99, AvgValue = 99d }, new { MetricName = "mem", SampleCount = 1, AvgValue = 10d } }, rows);
    }

    [Fact]
    public async Task R462_AHealthRollupAnotherSweepWroteFirst_IsKept_AndTheOtherApplicationsStillGoIn()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAppAsync(ct);
        var second = await SeedAppAsync(ct);
        await using (var seed = NewContext())
        {
            seed.AppHealthSamples.AddRange(
                new AppHealthSample { MonitoredAppId = first, Timestamp = Hour.AddMinutes(1), IsUp = true, ResponseTimeMs = 20 },
                new AppHealthSample { MonitoredAppId = second, Timestamp = Hour.AddMinutes(2), IsUp = false });
            await seed.SaveChangesAsync(ct);
        }

        var otherColour = new CommitBeforeSave<AppHealthHourly>(async token =>
        {
            await using var other = NewContext();
            other.AppHealthHourly.Add(new AppHealthHourly { MonitoredAppId = first, HourUtc = Hour, SampleCount = 99, UpCount = 99, AvgResponseTimeMs = 20 });
            await other.SaveChangesAsync(token);
        });
        await using (var db = NewContext(otherColour))
        {
            var written = await new AppMonitoringRepository(db).AggregateHourAsync(Hour, ct);
            Assert.True(otherColour.Fired);
            Assert.Equal(1, written);
        }

        await using var read = NewContext();
        var rows = await read.AppHealthHourly.AsNoTracking()
            .Where(h => h.HourUtc == Hour)
            .OrderBy(h => h.MonitoredAppId)
            .Select(h => new { h.MonitoredAppId, h.UpCount })
            .ToListAsync(ct);
        Assert.Equal(new[] { new { MonitoredAppId = first, UpCount = 99 }, new { MonitoredAppId = second, UpCount = 0 } }, rows);
    }

    private AppDbContext NewContext(IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).AddInterceptors(interceptor).Options);

    private async Task<int> SeedAppAsync(CancellationToken ct)
    {
        await using var db = NewContext();
        var suffix = Guid.NewGuid().ToString("N");
        var organization = new Organization { Name = $"Rollup {suffix}", Slug = $"rollup-{suffix}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = $"Rollup {suffix}", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var app = new MonitoredApp { ProjectId = project.Id, Name = "app" };
        db.MonitoredApps.Add(app);
        await db.SaveChangesAsync(ct);
        return app.Id;
    }

    /// <summary>The other colour's commit, run once, when this context first saves rollup rows: after
    /// the sweep read the hour, before its INSERT reaches the database.</summary>
    private sealed class CommitBeforeSave<TRow>(Func<CancellationToken, Task> otherWriter) : SaveChangesInterceptor
        where TRow : class
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<TRow>().Any(e => e.State == EntityState.Added))
            {
                Fired = true;
                await otherWriter(cancellationToken);
            }
            return result;
        }
    }
}
