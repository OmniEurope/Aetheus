// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Alerts;

/// <summary>
/// PLAN-010 lot 1: every server gets the two clock offset guards, at the configured thresholds, and an
/// operator's change to one of them survives the next reconciliation.
/// </summary>
public sealed class ClockAlertProvisioningTests
{
    private static async Task<(ServiceProvider Provider, int ServerId)> CreateAsync()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString("N");
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAlertRepository, AlertRepository>();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var server = new Server { Name = "prod-01", Hostname = "prod-01" };
        db.Servers.Add(server);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (provider, server.Id);
    }

    private static StorageAlertProvisioningService CreateService(ServiceProvider provider, Dictionary<string, string?> settings) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<StorageAlertProvisioningService>.Instance);

    private static async Task<List<AlertRule>> RulesAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AlertRules
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProvisionAsync_CreatesTheTwoClockGuardsAtTheConfiguredThresholds()
    {
        var (provider, serverId) = await CreateAsync();
        await using var owner = provider;
        var service = CreateService(provider, new()
        {
            ["StorageMonitoring:ProvisionDefaultAlerts"] = "false",
            ["ClockMonitoring:WarningSeconds"] = "2",
            ["ClockMonitoring:CriticalSeconds"] = "45"
        });

        await service.ProvisionAsync(TestContext.Current.CancellationToken);
        await service.ProvisionAsync(TestContext.Current.CancellationToken);

        var rules = await RulesAsync(provider);
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, rule => rule.ProvisioningKey == $"clock-offset-warning:{serverId}"
            && rule.Metric == MetricType.ClockOffset && rule.Operator == ComparisonOperator.GreaterThanOrEqual
            && rule.Threshold == 2 && rule.Severity == AlertSeverity.Warning && rule.IsEnabled);
        Assert.Contains(rules, rule => rule.ProvisioningKey == $"clock-offset-critical:{serverId}"
            && rule.Metric == MetricType.ClockOffset && rule.Threshold == 45 && rule.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public async Task ProvisionAsync_DefaultsToOneAndThirtySeconds_BesideTheStorageAndInodeGuards()
    {
        var (provider, _) = await CreateAsync();
        await using var owner = provider;

        await CreateService(provider, []).ProvisionAsync(TestContext.Current.CancellationToken);

        var rules = await RulesAsync(provider);
        Assert.Equal(7, rules.Count);
        Assert.Equal(2, rules.Count(rule => rule.Metric == MetricType.Inodes));
        Assert.Equal([1d, 30d], rules.Where(rule => rule.Metric == MetricType.ClockOffset)
            .Select(rule => rule.Threshold).Order());
    }

    [Fact]
    public async Task ProvisionAsync_KeepsAnOperatorsClockRule()
    {
        var (provider, serverId) = await CreateAsync();
        await using var owner = provider;
        var service = CreateService(provider, new() { ["StorageMonitoring:ProvisionDefaultAlerts"] = "false" });
        await service.ProvisionAsync(TestContext.Current.CancellationToken);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var warning = await db.AlertRules.SingleAsync(
                rule => rule.ProvisioningKey == $"clock-offset-warning:{serverId}", TestContext.Current.CancellationToken);
            warning.Threshold = 5;
            warning.IsEnabled = false;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await service.ProvisionAsync(TestContext.Current.CancellationToken);

        var rules = await RulesAsync(provider);
        Assert.Equal(2, rules.Count);
        var kept = Assert.Single(rules, rule => rule.ProvisioningKey == $"clock-offset-warning:{serverId}");
        Assert.Equal(5, kept.Threshold);
        Assert.False(kept.IsEnabled);
    }

    [Fact]
    public async Task ProvisionAsync_CreatesNoClockGuard_WhenDisabled()
    {
        var (provider, _) = await CreateAsync();
        await using var owner = provider;

        await CreateService(provider, new() { ["ClockMonitoring:ProvisionDefaultAlerts"] = "false" })
            .ProvisionAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(await RulesAsync(provider), rule => rule.Metric == MetricType.ClockOffset);
    }
}
