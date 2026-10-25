// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// BACK-004 on real PostgreSQL: two evaluators (blue and green during a swap) read the same rule and
/// the same cooldown at once. The reservation is a conditional UPDATE committed before emission, so
/// exactly one of them notifies.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AlertCooldownReservationIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task TwoConcurrentEvaluators_EmitTheAlertOnce()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var ruleId = await SeedBreachedRuleAsync(ct);
        var notifications = Substitute.For<INotificationService>();

        await Task.WhenAll(NewEvaluator(notifications).EvaluateAlertRulesAsync(ct),
            NewEvaluator(notifications).EvaluateAlertRulesAsync(ct));

        await notifications.Received(1).SendEventAsync("alert.triggered", Arg.Any<object>(), Arg.Any<CancellationToken>());
        await using var verify = NewContext();
        Assert.NotNull(await verify.AlertRules.Where(rule => rule.Id == ruleId).Select(rule => rule.LastTriggeredAt).SingleAsync(ct));
        // R-52, on real PostgreSQL: the alert that was emitted once is stored once, and its identifier is the one in the event.
        var stored = await verify.AlertOccurrences.SingleAsync(ct);
        Assert.Equal(ruleId, stored.AlertRuleId);
        await notifications.Received(1).SendEventAsync(
            "alert.triggered",
            Arg.Is<object>(payload => System.Text.Json.JsonSerializer.Serialize(payload).Contains($"\"AlertId\":{stored.Id},", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    private AlertEvaluatorService NewEvaluator(INotificationService notifications)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => NewContext())
            .AddScoped<IAlertRepository>(provider => new AlertRepository(provider.GetRequiredService<AppDbContext>(), TimeProvider.System))
            .AddScoped<IAlertOccurrenceRepository>(provider => new AlertOccurrenceRepository(provider.GetRequiredService<AppDbContext>()))
            .AddScoped<IAlertOccurrenceService>(provider => new AlertOccurrenceService(provider.GetRequiredService<IAlertOccurrenceRepository>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertOccurrenceService>.Instance))
            .AddSingleton(notifications)
            .BuildServiceProvider();
        return new AlertEvaluatorService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IHubContext<AlertHub>>(),
            NullLogger<AlertEvaluatorService>.Instance,
            TimeProvider.System);
    }

    private async Task<int> SeedBreachedRuleAsync(CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = $"alert-race-{Guid.NewGuid():N}", Slug = $"a{Guid.NewGuid():N}"[..12] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var server = new Server { Name = "alert-race", Hostname = "alert-race.test", OrganizationId = organization.Id };
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        var now = DateTime.UtcNow;
        db.ServerMetrics.AddRange(Enumerable.Range(0, 5).Select(step => new ServerMetric
        {
            ServerId = server.Id,
            Timestamp = now.AddSeconds(-30 * step),
            CpuPercent = 95
        }));
        var rule = new AlertRule
        {
            Name = "cpu-race",
            ServerId = server.Id,
            Metric = MetricType.Cpu,
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 80,
            SustainedSeconds = 60
        };
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule.Id;
    }
}
