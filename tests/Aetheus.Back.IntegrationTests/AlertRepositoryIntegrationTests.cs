// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.IntegrationTests;

public sealed class AlertRepositoryIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task GetRecentMetricsForServersAsync_ProjectsMultipleServersInOnePostgresQuery()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var organization = new Organization
        {
            Name = $"alerts-{Guid.NewGuid():N}",
            Slug = $"a{Guid.NewGuid():N}"[..12]
        };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var servers = new[]
        {
            new Server { Name = "alert-a", Hostname = "alert-a.test", OrganizationId = organization.Id },
            new Server { Name = "alert-b", Hostname = "alert-b.test", OrganizationId = organization.Id }
        };
        db.Servers.AddRange(servers);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        db.ServerMetrics.AddRange(
            servers.SelectMany(server => Enumerable.Range(0, 3).Select(index => new ServerMetric
            {
                ServerId = server.Id,
                Timestamp = now.AddSeconds(-index * 30),
                CpuPercent = 50 + index
            })));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new AlertRepository(db, TimeProvider.System)
            .GetRecentMetricsForServersAsync(
                servers.Select(server => server.Id).ToArray(),
                120,
                TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.All(result.Values, metrics => Assert.Equal(3, metrics.Count));
        Assert.All(result.Values, metrics =>
            Assert.Equal(metrics.OrderByDescending(metric => metric.Timestamp), metrics));
    }
}
