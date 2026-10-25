// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Exercises <see cref="DemoDataSeeder.SeedDemoAsync"/> on real PostgreSQL: it must populate the
/// demo domain data across the main local/QA pages under the default org and be idempotent
/// (the startup path re-runs it on every boot, so existing rows must be reconciled without duplication).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DemoDataSeederIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private const string StrongAdminPassword = "Integr@tion-Seed-Admin-Pwd-2026";

    private static IConfiguration ConfigWith(string adminPassword) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:AdminPassword"] = adminPassword })
            .Build();

    [Fact]
    public async Task SeedDemoAsync_OnBootstrappedDatabase_PopulatesDemoDomainData()
    {
        // Arrange - bootstrap seed first so the default "aetheus" org exists.
        await ResetAndMigrateAsync();
        await using (var bootstrap = NewContext())
            await DbInitializer.SeedAsync(bootstrap, ConfigWith(StrongAdminPassword));

        // Act
        await using (var db = NewContext())
            await DemoDataSeeder.SeedDemoAsync(db, TimeProvider.System);

        // Assert - demo servers, projects, pipelines and alerts are present and wired to the org.
        await using var verify = NewContext();
        var orgId = await verify.Organizations.Where(o => o.Slug == "aetheus").Select(o => o.Id).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, await verify.Servers.CountAsync(s => s.OrganizationId == orgId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await verify.Servers.CountAsync(
            s => s.OrganizationId == orgId && s.Status == Aetheus.Shared.Components.Servers.ServerStatus.Offline,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await verify.ServiceInfos.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.DockerContainers.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.DockerImages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.DockerNetworks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.DockerVolumes.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.DockerComposeStacks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await verify.Projects.CountAsync(p => p.OrganizationId == orgId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await verify.PipelineTemplates.CountAsync(template =>
            template.Name == "Demo Web Delivery"
            || template.Name == "Demo API Validation"
            || template.Name == "Demo Toto Release", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(5, await verify.Pipelines.CountAsync(p => p.ProjectId != null, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.AlertRules.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await verify.AppSettings.AnyAsync(s => s.Key == "DemoDataSeeded:2026-07-14", cancellationToken: TestContext.Current.CancellationToken));

        // DM4R history rows must seed too (metric charts, run timeline, release/changelog tiles):
        // 2 monitored servers x 25 hourly samples; 5 pipelines x 3 runs; 4 published releases.
        Assert.Equal(50, await verify.ServerMetrics.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(15, await verify.PipelineRuns.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(4, await verify.Releases.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await verify.Environments.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await verify.ProjectServers.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await verify.MonitoredApps.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.NotificationChannels.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.WorkItems.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.VariableLibraries.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(4, await verify.VariableLibraryEntries.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.Vaults.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.Tasks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.TaskLogs.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.Dashboards.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(6, await verify.DashboardWidgets.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.BackupPolicies.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.BackupRuns.CountAsync(cancellationToken: TestContext.Current.CancellationToken));

        // Every demo pipeline is owned by a demo project (exactly-one-owner contract).
        Assert.True(await verify.Pipelines.AllAsync(p => p.ProjectId != null, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeedDemoAsync_RunTwice_IsIdempotent_NoDuplicateDemoRows()
    {
        // Arrange
        await ResetAndMigrateAsync();
        await using (var bootstrap = NewContext())
            await DbInitializer.SeedAsync(bootstrap, ConfigWith(StrongAdminPassword));

        // Act - two demo seeds on fresh contexts; natural keys must keep the second pass idempotent.
        await using (var first = NewContext())
            await DemoDataSeeder.SeedDemoAsync(first, TimeProvider.System);
        await using (var second = NewContext())
            await DemoDataSeeder.SeedDemoAsync(second, TimeProvider.System);

        // Assert - counts unchanged by the second run.
        await using var verify = NewContext();
        Assert.Equal(3, await verify.Servers.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await verify.Projects.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await verify.PipelineTemplates.CountAsync(template =>
            template.Name == "Demo Web Delivery"
            || template.Name == "Demo API Validation"
            || template.Name == "Demo Toto Release", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(5, await verify.Pipelines.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.AlertRules.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.AppSettings.CountAsync(s => s.Key == "DemoDataSeeded:2026-07-14", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeedDemoAsync_WithoutBootstrapOrg_IsNoOp()
    {
        // Arrange - pristine schema. The ownership migration backfills the "aetheus" org, so to
        // exercise the no-org guard we remove it first, then confirm the seeder writes nothing.
        await ResetAndMigrateAsync();
        await using (var prep = NewContext())
        {
            await prep.Organizations.Where(o => o.Slug == "aetheus").ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        // Act
        await using (var db = NewContext())
            await DemoDataSeeder.SeedDemoAsync(db, TimeProvider.System);

        // Assert - nothing seeded, no marker written.
        await using var verify = NewContext();
        Assert.Equal(0, await verify.Servers.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await verify.AppSettings.AnyAsync(s => s.Key == "DemoDataSeeded:2026-07-14", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeedDemoAsync_WithMarkerAndMissingRows_ReconcilesDatedDataset()
    {
        await ResetAndMigrateAsync();
        await using (var bootstrap = NewContext())
            await DbInitializer.SeedAsync(bootstrap, ConfigWith(StrongAdminPassword));
        await using (var first = NewContext())
            await DemoDataSeeder.SeedDemoAsync(first, TimeProvider.System);

        await using (var partial = NewContext())
        {
            await partial.Vaults.ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            await partial.DashboardWidgets.ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            await partial.Dashboards.ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            await partial.BackupRuns.ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            await partial.BackupPolicies.ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            await partial.PipelineTemplates
                .Where(template => template.Name == "Demo Toto Release")
                .ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using (var repair = NewContext())
            await DemoDataSeeder.SeedDemoAsync(repair, TimeProvider.System);

        await using var verify = NewContext();
        Assert.Equal(2, await verify.Vaults.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.Dashboards.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(6, await verify.DashboardWidgets.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.BackupPolicies.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.BackupRuns.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await verify.PipelineTemplates.AnyAsync(template => template.Name == "Demo Toto Release", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.AppSettings.CountAsync(s => s.Key == "DemoDataSeeded:2026-07-14", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeedDemoAsync_UnknownDatasetDate_FailsClosed()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            DemoDataSeeder.SeedDemoAsync(db, TimeProvider.System, "2026-07-15"));

        Assert.Contains("Unsupported demo seed date", error.Message);
        Assert.False(await db.AppSettings.AnyAsync(setting => setting.Key.StartsWith("DemoDataSeeded:"), cancellationToken: TestContext.Current.CancellationToken));
    }
}
