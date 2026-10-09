// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-013 lot 6 on PostgreSQL: after a purge the table keeps its size on disk; « Récupérer la place »
/// runs the real <c>VACUUM FULL</c> and the size measured after is smaller than before, while a running
/// pipeline refuses it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReclaimSpaceIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task AfterAPurge_ReclaimingTheSpace_ShrinksTheTable()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var appId = await SeedSamplesAsync(20_000, ct);
        await using (var db = NewContext())
            await db.AppMetricSamples.Where(sample => sample.MonitoredAppId == appId && sample.Value > 100).ExecuteDeleteAsync(ct);

        var services = new ServiceCollection();
        services.AddScoped<AppDbContext>(_ => NewContext());
        services.AddScoped<IReclaimSpaceRepository, ReclaimSpaceRepository>();
        services.AddScoped(_ => Substitute.For<IAuditService>());
        await using var provider = services.BuildServiceProvider();
        var tracker = new ReclaimSpaceTracker();
        await using var scope = provider.CreateAsyncScope();
        var service = new ReclaimSpaceService(
            scope.ServiceProvider.GetRequiredService<IReclaimSpaceRepository>(), tracker,
            provider.GetRequiredService<IServiceScopeFactory>(), Substitute.For<IAuditService>(), TimeProvider.System,
            NullLogger<ReclaimSpaceService>.Instance);

        var status = await service.GetStatusAsync(ct);
        Assert.Equal(5, status.Tables.Count);
        await service.StartAsync("AppMetricSamples", "admin", ct);
        await tracker.Current;

        var result = Assert.Single(tracker.History);
        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.BytesAfter < result.BytesBefore, $"{result.BytesBefore} bytes before, {result.BytesAfter} after");
        await using var check = NewContext();
        Assert.Equal(100, await check.AppMetricSamples.CountAsync(ct));
    }

    [Fact]
    public async Task WhileAPipelineRuns_NothingIsRewritten()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        await using (var db = NewContext())
        {
            var organization = new Organization { Name = "Busy", Slug = $"busy-{Guid.NewGuid():N}" };
            db.Organizations.Add(organization);
            await db.SaveChangesAsync(ct);
            var project = new Project { Name = "Busy", OrganizationId = organization.Id };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            var pipeline = new Pipeline { Name = "ci", ProjectId = project.Id, YamlDefinition = "name: ci\ntrigger: manual\nstages: []" };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(ct);
            db.PipelineRuns.Add(new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
        }

        await using var context = NewContext();
        var service = new ReclaimSpaceService(
            new ReclaimSpaceRepository(context), new ReclaimSpaceTracker(),
            Substitute.For<IServiceScopeFactory>(), Substitute.For<IAuditService>(), TimeProvider.System,
            NullLogger<ReclaimSpaceService>.Instance);

        await Assert.ThrowsAsync<ConflictException>(() => service.StartAsync("AnalysisMetrics", "admin", ct));
    }

    private async Task<int> SeedSamplesAsync(int count, CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = "Reclaim", Slug = $"reclaim-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = "Reclaim", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var app = new MonitoredApp { ProjectId = project.Id, Name = "reclaim" };
        db.MonitoredApps.Add(app);
        await db.SaveChangesAsync(ct);
        db.AppMetricSamples.AddRange(Enumerable.Range(1, count).Select(index => new AppMetricSample
        {
            MonitoredAppId = app.Id,
            MetricName = "cpu",
            Timestamp = DateTime.UtcNow.AddSeconds(-index),
            Value = index,
            AttributesJson = "{\"route\":\"/api/some/long/route/to/fill/the/page\"}"
        }));
        await db.SaveChangesAsync(ct);
        return app.Id;
    }
}
