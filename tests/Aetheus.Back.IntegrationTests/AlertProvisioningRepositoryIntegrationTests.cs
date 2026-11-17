// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

public sealed class AlertProvisioningRepositoryIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task AddProvisionedRulesIfMissingAsync_ConcurrentPostgresCallsInsertEachKeyOnce()
    {
        await fixture.ResetAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        var serverId = await SeedServerAsync(options);
        var rules = CreateRules(serverId);
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        await using var firstContext = new AppDbContext(options);
        await using var secondContext = new AppDbContext(options);
        var first = new AlertRepository(firstContext, new FixedTimeProvider(now));
        var second = new AlertRepository(secondContext, new FixedTimeProvider(now));

        var results = await Task.WhenAll(
            first.AddProvisionedRulesIfMissingAsync(
                rules.Select(CloneRule).ToArray(),
                TestContext.Current.CancellationToken),
            second.AddProvisionedRulesIfMissingAsync(
                rules.Select(CloneRule).ToArray(),
                TestContext.Current.CancellationToken));

        Assert.Equal(3, results.Sum());
        await using var verification = new AppDbContext(options);
        var stored = await verification.AlertRules
            .Where(rule => rule.ServerId == serverId)
            .OrderBy(rule => rule.ProvisioningKey)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, stored.Count);
        Assert.Equal(3, stored.Select(rule => rule.ProvisioningKey).Distinct().Count());
    }

    private static async Task<int> SeedServerAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var organizationId = await db.Organizations
            .OrderBy(organization => organization.Id)
            .Select(organization => organization.Id)
            .FirstAsync(TestContext.Current.CancellationToken);
        var server = new Server
        {
            OrganizationId = organizationId,
            Name = $"provisioning-{Guid.NewGuid():N}",
            Hostname = "provisioning.local",
            Status = ServerStatus.Online
        };
        db.Servers.Add(server);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return server.Id;
    }

    private static AlertRule[] CreateRules(int serverId) =>
    [
        CreateRule(serverId, "storage-disk-warning", MetricType.Disk, 80, AlertSeverity.Warning),
        CreateRule(serverId, "storage-disk-critical", MetricType.Disk, 90, AlertSeverity.Critical),
        CreateRule(serverId, "storage-disk-free", MetricType.DiskFree, 20, AlertSeverity.Critical)
    ];

    private static AlertRule CreateRule(
        int serverId,
        string kind,
        MetricType metric,
        double threshold,
        AlertSeverity severity) =>
        new()
        {
            Name = $"{kind}-{serverId}",
            ProvisioningKey = $"{kind}:{serverId}",
            ServerId = serverId,
            Metric = metric,
            Operator = metric == MetricType.DiskFree
                ? ComparisonOperator.LessThan
                : ComparisonOperator.GreaterThanOrEqual,
            Threshold = threshold,
            SustainedSeconds = 60,
            Severity = severity,
            IsEnabled = true
        };

    private static AlertRule CloneRule(AlertRule rule) =>
        new()
        {
            Name = rule.Name,
            ProvisioningKey = rule.ProvisioningKey,
            ServerId = rule.ServerId,
            Metric = rule.Metric,
            Operator = rule.Operator,
            Threshold = rule.Threshold,
            SustainedSeconds = rule.SustainedSeconds,
            Severity = rule.Severity,
            IsEnabled = rule.IsEnabled
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
