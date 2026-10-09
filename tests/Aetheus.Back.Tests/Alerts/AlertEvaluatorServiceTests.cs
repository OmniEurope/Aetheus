// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class AlertEvaluatorServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EvaluateAlertRulesAsync_LoadsAllServerWindowsInOneRepositoryCall()
    {
        var repository = Substitute.For<IAlertRepository>();
        repository.GetEnabledAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new AlertRule { ServerId = 11, SustainedSeconds = 60 },
            new AlertRule { ServerId = 12, SustainedSeconds = 180 }
        ]);
        repository.GetRecentMetricsForServersAsync(
                Arg.Any<IReadOnlyCollection<int>>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, List<ServerMetric>>());
        var services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(Substitute.For<INotificationService>())
            .BuildServiceProvider();
        var service = new AlertEvaluatorService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IHubContext<AlertHub>>(),
            NullLogger<AlertEvaluatorService>.Instance,
            TimeProvider.System);

        await service.EvaluateAlertRulesAsync(TestContext.Current.CancellationToken);

        await repository.Received(1).GetRecentMetricsForServersAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 2
                && ids.Contains(11)
                && ids.Contains(12)),
            270,
            TestContext.Current.CancellationToken);
        await repository.DidNotReceive().GetRecentMetricsAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAlertRulesAsync_ReservesTheCooldownBeforeEmitting()
    {
        var (repository, notifications, service) = BreachedRuleEvaluator(reservationWon: true);

        await service.EvaluateAlertRulesAsync(TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            repository.TryReserveTriggerAsync(5, null, Now, Arg.Any<CancellationToken>());
            notifications.SendEventAsync("alert.triggered", Arg.Any<object>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task EvaluateAlertRulesAsync_ReservationLostToAnotherEvaluator_EmitsNothing()
    {
        var (_, notifications, service) = BreachedRuleEvaluator(reservationWon: false);

        await service.EvaluateAlertRulesAsync(TestContext.Current.CancellationToken);

        await notifications.DidNotReceive().SendEventAsync(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    private static (IAlertRepository Repository, INotificationService Notifications, AlertEvaluatorService Service)
        BreachedRuleEvaluator(bool reservationWon)
    {
        var repository = Substitute.For<IAlertRepository>();
        repository.GetEnabledAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new AlertRule
            {
                Id = 5, ServerId = 11, SustainedSeconds = 60, Metric = MetricType.Cpu,
                Operator = ComparisonOperator.GreaterThan, Threshold = 80
            }
        ]);
        repository.GetRecentMetricsForServersAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, List<ServerMetric>>
            {
                [11] = [.. Enumerable.Range(0, 4).Select(step => new ServerMetric
                {
                    ServerId = 11, Timestamp = Now.AddSeconds(-30 * (3 - step)), CpuPercent = 95
                })]
            });
        repository.TryReserveTriggerAsync(5, null, Now, Arg.Any<CancellationToken>()).Returns(reservationWon);
        var notifications = Substitute.For<INotificationService>();
        var services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(notifications)
            .BuildServiceProvider();
        var service = new AlertEvaluatorService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IHubContext<AlertHub>>(),
            NullLogger<AlertEvaluatorService>.Instance,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(Now)));
        return (repository, notifications, service);
    }

    [Fact]
    public void HasContinuousCoverage_RequiresBoundaryAndRecentMetric()
    {
        var metrics = new List<ServerMetric>
        {
            new() { Timestamp = Now.AddMinutes(-5) },
            new() { Timestamp = Now.AddMinutes(-4) },
            new() { Timestamp = Now.AddMinutes(-3) },
            new() { Timestamp = Now.AddMinutes(-2) },
            new() { Timestamp = Now.AddMinutes(-1) },
            new() { Timestamp = Now }
        };

        Assert.True(AlertEvaluatorService.HasContinuousCoverage(metrics, Now.AddMinutes(-5), Now));
        Assert.False(AlertEvaluatorService.HasContinuousCoverage(
            metrics[1..], Now.AddMinutes(-5), Now));
        Assert.False(AlertEvaluatorService.HasContinuousCoverage(
            metrics[..^2], Now.AddMinutes(-5), Now));
    }

    [Fact]
    public void HasContinuousCoverage_RejectsGapLongerThanTelemetryTolerance()
    {
        var metrics = new List<ServerMetric>
        {
            new() { Timestamp = Now.AddMinutes(-5) },
            new() { Timestamp = Now.AddMinutes(-4) },
            new() { Timestamp = Now.AddMinutes(-1) },
            new() { Timestamp = Now }
        };

        Assert.False(AlertEvaluatorService.HasContinuousCoverage(metrics, Now.AddMinutes(-5), Now));
    }

    // --- IsThresholdBreached CPU ---

    [Theory]
    [InlineData(ComparisonOperator.GreaterThan, 80, 85, true)]
    [InlineData(ComparisonOperator.GreaterThan, 80, 80, false)]
    [InlineData(ComparisonOperator.GreaterThan, 80, 75, false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 80, 80, true)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 80, 79, false)]
    [InlineData(ComparisonOperator.LessThan, 20, 15, true)]
    [InlineData(ComparisonOperator.LessThan, 20, 20, false)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 20, 20, true)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 20, 21, false)]
    public void IsThresholdBreached_CpuMetric_EvaluatesCorrectly(
        ComparisonOperator op, double threshold, double cpuPercent, bool expected)
    {
        var rule = new AlertRule { Metric = MetricType.Cpu, Operator = op, Threshold = threshold };
        var metric = new ServerMetric { CpuPercent = cpuPercent };

        var result = AlertEvaluatorService.IsThresholdBreached(rule, metric);

        Assert.Equal(expected, result);
    }

    // --- IsThresholdBreached Memory ---

    [Fact]
    public void IsThresholdBreached_MemoryPercent_CalculatesFromUsedAndTotal()
    {
        var rule = new AlertRule
        {
            Metric = MetricType.Memory,
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 80
        };
        // 9000 / 10000 = 90% > 80
        var metric = new ServerMetric { MemoryUsedMb = 9000, MemoryTotalMb = 10000 };

        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void IsThresholdBreached_MemoryZeroTotal_ReturnsZeroPercent()
    {
        var rule = new AlertRule
        {
            Metric = MetricType.Memory,
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 0.1
        };
        var metric = new ServerMetric { MemoryUsedMb = 100, MemoryTotalMb = 0 };

        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    // --- IsThresholdBreached Disk ---

    [Fact]
    public void IsThresholdBreached_DiskPercent_CalculatesFromUsedAndTotal()
    {
        var rule = new AlertRule
        {
            Metric = MetricType.Disk,
            Operator = ComparisonOperator.GreaterThanOrEqual,
            Threshold = 90
        };
        // 450 / 500 = 90% >= 90
        var metric = new ServerMetric { DiskUsedGb = 450, DiskTotalGb = 500 };

        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void IsThresholdBreached_DiskZeroTotal_ReturnsZeroPercent()
    {
        var rule = new AlertRule
        {
            Metric = MetricType.Disk,
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 0.1
        };
        var metric = new ServerMetric { DiskUsedGb = 100, DiskTotalGb = 0 };

        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    // --- Unknown metric / operator ---

    [Fact]
    public void IsThresholdBreached_UnknownMetric_ReturnsFalse()
    {
        var rule = new AlertRule
        {
            Metric = (MetricType)999,
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 50
        };
        var metric = new ServerMetric { CpuPercent = 99 };

        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void IsThresholdBreached_UnknownOperator_ReturnsFalse()
    {
        var rule = new AlertRule
        {
            Metric = MetricType.Cpu,
            Operator = (ComparisonOperator)999,
            Threshold = 50
        };
        var metric = new ServerMetric { CpuPercent = 99 };

        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }
}
