// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-455, R-458, R-463 on PostgreSQL: the unit tests run the InMemory provider, which neither translates
/// these queries nor runs the bulk deletes. Here the hour-by-hour rollup check, the batched purges, the
/// latest-export read and the delete by identifier run as production runs them.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TelemetryRetentionAndPerformanceIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static readonly DateTime CurrentHour = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private async Task<int> SeedAppAsync(CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = "Telemetry", Slug = $"telemetry-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = "Telemetry", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var app = new MonitoredApp { ProjectId = project.Id, Name = "app" };
        db.MonitoredApps.Add(app);
        await db.SaveChangesAsync(ct);
        return app.Id;
    }

    [Fact]
    public async Task R458_TheRollupReadsOnlyMissingHours_AndThePurgesRunInBatches()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var appId = await SeedAppAsync(ct);
        var rolled = CurrentHour.AddHours(-3);
        var missing = CurrentHour.AddHours(-1);
        await using (var db = NewContext())
        {
            db.AppMetricSamples.AddRange(
                new AppMetricSample { MonitoredAppId = appId, MetricName = "cpu", Timestamp = rolled.AddMinutes(5), Value = 1 },
                new AppMetricSample { MonitoredAppId = appId, MetricName = "cpu", Timestamp = missing.AddMinutes(5), Value = 2, AttributesJson = "{\"k\":\"a\"}" },
                new AppMetricSample { MonitoredAppId = appId, MetricName = "cpu", Timestamp = missing.AddMinutes(9), Value = 4, AttributesJson = "{\"k\":\"a\"}" });
            db.AppMetricHourly.Add(new AppMetricHourly { MonitoredAppId = appId, MetricName = "cpu", HourUtc = rolled, SampleCount = 1 });
            // Expired rows, more than one batch of the purge.
            db.AppMetricSamples.AddRange(Enumerable.Range(0, AppMetricRepository.PurgeBatchSize + 3).Select(i => new AppMetricSample
            {
                MonitoredAppId = appId,
                MetricName = "old",
                Timestamp = CurrentHour.AddDays(-40).AddSeconds(i),
                Value = i
            }));
            await db.SaveChangesAsync(ct);
        }

        await using (var db = NewContext())
        {
            var repo = new AppMetricRepository(db);
            Assert.Equal(0, await repo.AggregateHourAsync(rolled, ct));
            Assert.Equal(1, await repo.AggregateHourAsync(missing, ct));
            Assert.Equal(0, await repo.AggregateHourAsync(missing, ct));
            Assert.Equal(AppMetricRepository.PurgeBatchSize + 3, await repo.PurgeRawOlderThanAsync(CurrentHour.AddDays(-30), [appId], ct));
        }

        await using var read = NewContext();
        var added = await read.AppMetricHourly.AsNoTracking().SingleAsync(h => h.HourUtc == missing, ct);
        Assert.Equal((2, 3d), (added.SampleCount, added.AvgValue));
        Assert.Equal(3, await read.AppMetricSamples.CountAsync(ct));
    }

    [Fact]
    public async Task R455_TheLatestExportOfTheRouteGauges_IsReadOnPostgres()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var appId = await SeedAppAsync(ct);
        const string route = """{"http.request.method":"GET","http.route":"api/orders"}""";
        await using (var db = NewContext())
        {
            db.AppMetricSamples.AddRange(
                new AppMetricSample { MonitoredAppId = appId, MetricName = RequestPerformanceMetrics.P95Metric, Timestamp = CurrentHour.AddMinutes(-10), Value = 900, AttributesJson = route },
                new AppMetricSample { MonitoredAppId = appId, MetricName = RequestPerformanceMetrics.P95Metric, Timestamp = CurrentHour, Value = 110, AttributesJson = route },
                new AppMetricSample { MonitoredAppId = appId, MetricName = RequestPerformanceMetrics.CountMetric, Timestamp = CurrentHour, Value = 42, AttributesJson = route });
            await db.SaveChangesAsync(ct);
        }

        await using var read = NewContext();
        var samples = await new AppMetricRepository(read).GetLatestExportAsync(
            appId, AppRoutePerformance.MetricNames, AppRoutePerformance.LatestPointLookback, ct);
        var report = AppRoutePerformance.Build(samples);

        var orders = Assert.Single(report.Routes);
        Assert.Equal(("GET", "api/orders", 110d, 42L), (orders.Method, orders.Route, orders.P95Ms!.Value, orders.Count!.Value));
    }

    [Fact]
    public async Task R468_TheAudienceOfSeveralApplications_IsAddedUpPerPeriodOnPostgres()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAppAsync(ct);
        int second, other;
        await using (var db = NewContext())
        {
            // Two more applications of the same project: the organization's name is unique.
            var projectId = await db.MonitoredApps.Where(app => app.Id == first).Select(app => app.ProjectId).SingleAsync(ct);
            var secondApp = new MonitoredApp { ProjectId = projectId, Name = "second" };
            var otherApp = new MonitoredApp { ProjectId = projectId, Name = "other" };
            db.MonitoredApps.AddRange(secondApp, otherApp);
            await db.SaveChangesAsync(ct);
            (second, other) = (secondApp.Id, otherApp.Id);
        }
        var today = new DateOnly(2026, 9, 30);
        var week = new DateOnly(2026, 9, 28);
        var month = new DateOnly(2026, 9, 1);
        await using (var db = NewContext())
        {
            db.AppAnalyticsAggregates.AddRange(
                new AppAnalyticsAggregate { MonitoredAppId = first, PeriodKind = AnalyticsPeriodKind.Day, PeriodStartUtc = today, UniqueVisitors = 2, Sessions = 3, PageViews = 10 },
                new AppAnalyticsAggregate { MonitoredAppId = second, PeriodKind = AnalyticsPeriodKind.Day, PeriodStartUtc = today, UniqueVisitors = 5, Sessions = 6, PageViews = 20 },
                new AppAnalyticsAggregate { MonitoredAppId = first, PeriodKind = AnalyticsPeriodKind.Day, PeriodStartUtc = today.AddDays(-1), UniqueVisitors = 100, Sessions = 100, PageViews = 100 },
                new AppAnalyticsAggregate { MonitoredAppId = first, PeriodKind = AnalyticsPeriodKind.Month, PeriodStartUtc = month, UniqueVisitors = 30, AuthenticatedUniqueVisitors = 4, Sessions = 50, PageViews = 400 },
                new AppAnalyticsAggregate { MonitoredAppId = second, PeriodKind = AnalyticsPeriodKind.Month, PeriodStartUtc = month, UniqueVisitors = 10, AuthenticatedUniqueVisitors = 3, Sessions = 40, PageViews = 200 },
                new AppAnalyticsAggregate { MonitoredAppId = other, PeriodKind = AnalyticsPeriodKind.Month, PeriodStartUtc = month, UniqueVisitors = 999, Sessions = 999, PageViews = 999 });
            await db.SaveChangesAsync(ct);
        }

        await using var read = NewContext();
        var totals = await new AppMonitoringRepository(read).GetAudienceTotalsAsync([first, second], today, week, month, ct);

        // No aggregate for the week: the period is absent, and the application left out is not counted.
        Assert.Equal(2, totals.Count);
        Assert.Equal(new AppAudiencePeriodTotal(AnalyticsPeriodKind.Day, 7, 0, 9, 30), totals.Single(total => total.Kind == AnalyticsPeriodKind.Day));
        Assert.Equal(new AppAudiencePeriodTotal(AnalyticsPeriodKind.Month, 40, 7, 90, 600), totals.Single(total => total.Kind == AnalyticsPeriodKind.Month));
    }

    [Fact]
    public async Task R498_TheRunsARunStarted_AreFoundByTheirParentMarker_WithinTheWindow()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var started = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
        int parentId, childId;
        await using (var db = NewContext())
        {
            var pipeline = new Pipeline { Name = $"lineage-{Guid.NewGuid():N}" };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(ct);
            var parent = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = started, BuildNumber = 1 };
            db.PipelineRuns.Add(parent);
            await db.SaveChangesAsync(ct);
            string Marker(int id) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["UPSTREAM_RUN_ID"] = id.ToString(), ["UPSTREAM_PIPELINE"] = "p" });
            var child = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = started.AddMinutes(5), BuildNumber = 2, AdditionalVariablesJson = Marker(parent.Id) };
            // Same parent, started long after the window; another parent whose id only starts alike.
            var late = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = started.AddDays(2), BuildNumber = 3, AdditionalVariablesJson = Marker(parent.Id) };
            var other = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = started.AddMinutes(6), BuildNumber = 4, AdditionalVariablesJson = Marker(parent.Id * 10 + 1) };
            db.PipelineRuns.AddRange(child, late, other);
            await db.SaveChangesAsync(ct);
            (parentId, childId) = (parent.Id, child.Id);
        }

        await using var read = NewContext();
        var reader = new PipelineRunLineageRepository(read);
        var downstream = await reader.GetDownstreamRunsAsync(parentId, started, started.AddMinutes(30), ct);

        var link = Assert.Single(downstream);
        Assert.Equal((childId, 2, PipelineStatus.Running), (link.RunId, link.BuildNumber, link.Status));
        Assert.Equal(new PipelineRunWindow(started, null), await reader.GetRunWindowAsync(parentId, ct));
    }

    [Fact]
    public async Task R463_DeletingAnArtifactAlreadyRemoved_IsNotAConcurrencyFailure()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int artifactId;
        await using (var db = NewContext())
        {
            var pipeline = new Pipeline { Name = $"cleanup-{Guid.NewGuid():N}" };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(ct);
            var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(ct);
            var artifact = new PipelineArtifact { PipelineRunId = run.Id, PipelineId = pipeline.Id, Name = "a", FilePath = "a.zip" };
            db.PipelineArtifacts.Add(artifact);
            await db.SaveChangesAsync(ct);
            artifactId = artifact.Id;
        }

        await using var first = NewContext();
        await using var second = NewContext();
        var selectedByFirst = await first.PipelineArtifacts.SingleAsync(a => a.Id == artifactId, ct);
        var selectedBySecond = await second.PipelineArtifacts.SingleAsync(a => a.Id == artifactId, ct);

        Assert.True(await new ArtifactRepository(first).RemoveAsync(selectedByFirst, ct));
        Assert.False(await new ArtifactRepository(second).RemoveAsync(selectedBySecond, ct));
    }
}
