// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Alerts;

public sealed class StorageAlertProvisioningServiceTests
{
    [Fact]
    public async Task ProvisionAsync_CreatesFiveIdempotentBaselineRulesPerServer()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString("N");
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAlertRepository, AlertRepository>();
        await using var provider = services.BuildServiceProvider();

        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Servers.Add(new Server { Name = "prod-01", Hostname = "prod-01" });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using (var verifySeedScope = provider.CreateAsyncScope())
        {
            var db = verifySeedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.Servers.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The clock guards have their own tests (ClockAlertProvisioningTests).
                ["ClockMonitoring:ProvisionDefaultAlerts"] = "false",
                ["StorageMonitoring:ProvisionDefaultAlerts"] = "true",
                ["StorageMonitoring:DiskWarningPercent"] = "80",
                ["StorageMonitoring:DiskCriticalPercent"] = "90",
                ["StorageMonitoring:MinFreeGiB"] = "20",
                ["StorageMonitoring:InodeWarningPercent"] = "85",
                ["StorageMonitoring:InodeCriticalPercent"] = "95"
            })
            .Build();
        var service = new StorageAlertProvisioningService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<StorageAlertProvisioningService>.Instance);

        await service.ProvisionAsync(TestContext.Current.CancellationToken);
        await using (var verifyFirstProvisionScope = provider.CreateAsyncScope())
        {
            var db = verifyFirstProvisionScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(5, await db.AlertRules.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        await service.ProvisionAsync(TestContext.Current.CancellationToken);

        await using var verifyScope = provider.CreateAsyncScope();
        var rules = await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>().AlertRules.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, rules.Count);
        Assert.Contains(rules, rule => rule.Metric == MetricType.Disk && rule.Threshold == 80 && rule.Severity == AlertSeverity.Warning);
        Assert.Contains(rules, rule => rule.Metric == MetricType.Disk && rule.Threshold == 90 && rule.Severity == AlertSeverity.Critical);
        Assert.Contains(rules, rule => rule.Metric == MetricType.DiskFree && rule.Threshold == 20 && rule.Operator == ComparisonOperator.LessThan);
        Assert.Contains(rules, rule => rule.Metric == MetricType.Inodes && rule.Threshold == 85 && rule.Severity == AlertSeverity.Warning
            && rule.Operator == ComparisonOperator.GreaterThanOrEqual && rule.ProvisioningKey!.StartsWith("storage-inodes-warning:", StringComparison.Ordinal));
        Assert.Contains(rules, rule => rule.Metric == MetricType.Inodes && rule.Threshold == 95 && rule.Severity == AlertSeverity.Critical
            && rule.ProvisioningKey!.StartsWith("storage-inodes-critical:", StringComparison.Ordinal));
        Assert.All(rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.ProvisioningKey)));
        // R-47: the rule name is shown to French users in Go, never in the binary unit Gio.
        var freeRule = Assert.Single(rules, rule => rule.Metric == MetricType.DiskFree);
        Assert.Contains("20 Go", freeRule.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("Gio", freeRule.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionAsync_PreservesCustomizedProvisionedRuleWithoutDuplicatingIt()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString("N");
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAlertRepository, AlertRepository>();
        await using var provider = services.BuildServiceProvider();
        int serverId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var server = new Server { Name = "prod-01", Hostname = "prod-01" };
            db.Servers.Add(server);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            serverId = server.Id;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["StorageMonitoring:ProvisionDefaultAlerts"] = "true",
                ["ClockMonitoring:ProvisionDefaultAlerts"] = "false"
            }).Build();
        var service = new StorageAlertProvisioningService(
            provider.GetRequiredService<IServiceScopeFactory>(), configuration,
            NullLogger<StorageAlertProvisioningService>.Instance);

        await service.ProvisionAsync(TestContext.Current.CancellationToken);
        await using (var customizeScope = provider.CreateAsyncScope())
        {
            var db = customizeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var warning = await db.AlertRules.SingleAsync(rule =>
                rule.ProvisioningKey == $"storage-disk-warning:{serverId}", cancellationToken: TestContext.Current.CancellationToken);
            warning.Name = "Seuil opérateur personnalisé";
            warning.Threshold = 75;
            warning.SustainedSeconds = 900;
            warning.Severity = AlertSeverity.Info;
            warning.IsEnabled = false;
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        await service.ProvisionAsync(TestContext.Current.CancellationToken);

        await using var verifyScope = provider.CreateAsyncScope();
        var rules = await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>().AlertRules.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, rules.Count);
        var customized = Assert.Single(rules, rule => rule.ProvisioningKey == $"storage-disk-warning:{serverId}");
        Assert.Equal(75, customized.Threshold);
        Assert.Equal(900, customized.SustainedSeconds);
        Assert.Equal(AlertSeverity.Info, customized.Severity);
        Assert.False(customized.IsEnabled);
    }
}
