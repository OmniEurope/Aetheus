// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.DataSeed;

public sealed class DemoDataSeederTests
{
    [Fact]
    public async Task SeedDemoAsync_DefaultOrganization_CreatesCoherentIdempotentDemoDataset()
    {
        await using var db = CreateContext();
        db.Organizations.Add(new Organization { Name = "Aetheus", Slug = "aetheus" });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var markerKey = DemoDataSeeder.GetMarkerKey(null);

        await DemoDataSeeder.SeedDemoAsync(db, clock);

        Assert.Equal(3, await db.Servers.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await db.Projects.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(5, await db.Pipelines.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(50, await db.ServerMetrics.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(15, await db.PipelineRuns.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(4, await db.Releases.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.AlertRules.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.BackupPolicies.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(now.UtcDateTime.ToString("O"),
            (await db.AppSettings.SingleAsync(x => x.Key == markerKey, cancellationToken: TestContext.Current.CancellationToken)).Value);
        Assert.All(await db.Servers.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), server => Assert.NotEqual(0, server.OrganizationId));
        Assert.Contains(await db.Servers.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), server =>
            server.Type == ServerType.Build && server.OsType == OsType.Windows);
        Assert.All(
            await db.Servers.Where(server => server.Name == "web-01" || server.Name == "build-01")
                .ToListAsync(cancellationToken: TestContext.Current.CancellationToken),
            server =>
            {
                Assert.Equal(ServerStatus.Offline, server.Status);
                Assert.False(server.PipelineRunnerEnabled);
            });
        var demoPipelines = await db.Pipelines
            .Where(pipeline => pipeline.Name == "Build & Deploy Site" || pipeline.Name == "API CI")
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, demoPipelines.Count);
        Assert.All(demoPipelines, pipeline =>
        {
            Assert.Contains("test -f dist/index.html", pipeline.YamlDefinition, StringComparison.Ordinal);
            Assert.DoesNotContain("echo deploy", pipeline.YamlDefinition, StringComparison.OrdinalIgnoreCase);
        });

        var webServer = await db.Servers.SingleAsync(
            server => server.Name == "web-01",
            cancellationToken: TestContext.Current.CancellationToken);
        webServer.Status = ServerStatus.Offline;
        clock.Advance(TimeSpan.FromMinutes(5));
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await DemoDataSeeder.SeedDemoAsync(db, clock);

        Assert.Equal(3, await db.Servers.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(50, await db.ServerMetrics.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.BackupPolicies.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await db.AppSettings.Where(x => x.Key == markerKey).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        await db.Entry(webServer).ReloadAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ServerStatus.Offline, webServer.Status);
        Assert.Equal(now.UtcDateTime, webServer.LastHeartbeat);
    }

    [Fact]
    public async Task SeedDemoAsync_MissingBootstrapOrganization_DoesNotCreateOrMarkDemoData()
    {
        await using var db = CreateContext();

        await DemoDataSeeder.SeedDemoAsync(db, TimeProvider.System);

        Assert.Empty(await db.Servers.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.Projects.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.DoesNotContain(await db.AppSettings.ToListAsync(cancellationToken: TestContext.Current.CancellationToken),
            x => x.Key == DemoDataSeeder.GetMarkerKey(null));
    }

    [Fact]
    public async Task SeedDemoAsync_SamePipelineNameInAnotherProject_CreatesDemoPipelineInCorrectProject()
    {
        await using var db = CreateContext();
        var bootstrap = new Organization { Name = "Aetheus", Slug = "aetheus" };
        var foreign = new Organization { Name = "Foreign", Slug = "foreign" };
        db.Organizations.AddRange(bootstrap, foreign);
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var foreignProject = new Project
        {
            Name = "Foreign site",
            OrganizationId = foreign.Id
        };
        db.Projects.Add(foreignProject);
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        db.Pipelines.Add(new Pipeline
        {
            Name = "Build & Deploy Site",
            ProjectId = foreignProject.Id,
            CreatedByUsername = "foreign",
            YamlDefinition = "name: foreign\nstages: []"
        });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await DemoDataSeeder.SeedDemoAsync(db, TimeProvider.System);

        Assert.Equal(6, await db.Pipelines.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        var demoProjectId = await db.Projects
            .Where(project => project.OrganizationId == bootstrap.Id && project.Name == "Site Vitrine")
            .Select(project => project.Id)
            .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(await db.Pipelines.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), pipeline =>
            pipeline.ProjectId == demoProjectId && pipeline.Name == "Build & Deploy Site");
        Assert.Contains(await db.Pipelines.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), pipeline =>
            pipeline.ProjectId == foreignProject.Id && pipeline.YamlDefinition.Contains("foreign"));
    }

    [Fact]
    public async Task SeedDemoAsync_NullDependencies_FailFast()
    {
        await using var db = CreateContext();

        await Assert.ThrowsAsync<ArgumentNullException>(() => DemoDataSeeder.SeedDemoAsync(null!, TimeProvider.System));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DemoDataSeeder.SeedDemoAsync(db, null!));
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }
}
