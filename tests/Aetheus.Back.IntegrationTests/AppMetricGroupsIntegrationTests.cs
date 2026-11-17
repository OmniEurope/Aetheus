// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The metric chart opens on the first attribute group, so the groups come back freshest first. The
/// unit test runs on the InMemory provider; this runs the grouped query (nullable key, Max per group
/// over two tables) on PostgreSQL, where a translation failure would only surface in production.
/// </summary>
public sealed class AppMetricGroupsIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task AttributeGroups_AreOrderedByTheirLastReportOnPostgres()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        int appId;
        await using (var db = NewContext())
        {
            var organization = new Organization { Name = "Metrics", Slug = $"metrics-{Guid.NewGuid():N}" };
            db.Organizations.Add(organization);
            await db.SaveChangesAsync(ct);
            var project = new Project { Name = "Metrics", OrganizationId = organization.Id };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            var app = new MonitoredApp { ProjectId = project.Id, Name = "app" };
            db.MonitoredApps.Add(app);
            await db.SaveChangesAsync(ct);
            appId = app.Id;

            db.AppMetricHourly.Add(new AppMetricHourly
            {
                MonitoredAppId = appId,
                MetricName = "cpu.time",
                AttributesJson = null,
                HourUtc = now.AddDays(-20),
                SampleCount = 1,
                AvgValue = 1
            });
            db.AppMetricSamples.AddRange(
                new AppMetricSample { MonitoredAppId = appId, MetricName = "cpu.time", Timestamp = now.AddDays(-3), Value = 1, AttributesJson = "{\"cpu.mode\":\"idle\"}" },
                new AppMetricSample { MonitoredAppId = appId, MetricName = "cpu.time", Timestamp = now.AddMinutes(-1), Value = 2, AttributesJson = "{\"cpu.mode\":\"user\"}" },
                new AppMetricSample { MonitoredAppId = appId, MetricName = "cpu.time", Timestamp = now.AddMinutes(-2), Value = 3, AttributesJson = "{\"cpu.mode\":\"system\"}" },
                new AppMetricSample { MonitoredAppId = appId, MetricName = "cpu.time", Timestamp = now.AddMinutes(-9), Value = 4, AttributesJson = "{\"cpu.mode\":\"user\"}" });
            await db.SaveChangesAsync(ct);
        }

        await using var read = NewContext();
        var groups = await new AppMetricRepository(read).GetMetricAttributeGroupsAsync(appId, "cpu.time", ct);

        Assert.Equal(
            ["{\"cpu.mode\":\"user\"}", "{\"cpu.mode\":\"system\"}", "{\"cpu.mode\":\"idle\"}", null],
            groups);
    }
}
