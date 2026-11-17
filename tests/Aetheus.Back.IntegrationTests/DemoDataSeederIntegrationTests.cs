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
public sealed class DemoDataSeederIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private const string StrongAdminPassword = "Integr@tion-Seed-Admin-Pwd-2026";

    private static IConfiguration ConfigWith(string adminPassword) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:AdminPassword"] = adminPassword })
            .Build();

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
}
