// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

public sealed class AppVisitorRepositoryIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task DailyCounts_TranslateAndExecuteOnProductionPostgresProvider()
    {
        await fixture.ResetAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var organizationId = await db.Organizations.OrderBy(item => item.Id)
            .Select(item => item.Id)
            .FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        var project = new Project { Name = "Visitor integration", OrganizationId = organizationId };
        db.Projects.Add(project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var app = new MonitoredApp { ProjectId = project.Id, Name = "portfolio" };
        db.MonitoredApps.Add(app);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var today = new DateOnly(2026, 7, 22);
        var now = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
        var repository = new AppVisitorRepository(db);
        await repository.RecordAsync(app.Id, today, "first", now, TestContext.Current.CancellationToken);
        await repository.RecordAsync(app.Id, today, "second", now, TestContext.Current.CancellationToken);
        await repository.RecordAsync(app.Id, today.AddDays(-1), "previous", now.AddDays(-1), TestContext.Current.CancellationToken);

        var counts = await repository.GetDailyCountsAsync(app.Id, today.AddDays(-1), TestContext.Current.CancellationToken);

        Assert.Collection(counts,
            previous =>
            {
                Assert.Equal(today.AddDays(-1), previous.DayUtc);
                Assert.Equal(1, previous.UniqueVisitors);
            },
            current =>
            {
                Assert.Equal(today, current.DayUtc);
                Assert.Equal(2, current.UniqueVisitors);
            });
    }
}
