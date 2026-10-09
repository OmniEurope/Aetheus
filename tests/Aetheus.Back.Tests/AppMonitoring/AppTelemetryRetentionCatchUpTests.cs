// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>
/// R2-020: the startup catch-up read its whole 48-hour window of raw metrics at once and raised an
/// OutOfMemoryException in a 1 GB container. The sweep now rolls up one hour at a time, each hour in its
/// own scope, so what one hour loads is released before the next.
/// </summary>
public sealed class AppTelemetryRetentionCatchUpTests : IDisposable
{
    private static readonly DateTime CurrentHour = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);

    private readonly IAppMonitoringRepository _health = Substitute.For<IAppMonitoringRepository>();
    private readonly IAppMetricRepository _metrics = Substitute.For<IAppMetricRepository>();
    private readonly IAppLogRepository _logs = Substitute.For<IAppLogRepository>();
    private readonly IAppDataRetentionRepository _retention = Substitute.For<IAppDataRetentionRepository>();
    private int _healthScopes;
    private ServiceProvider? _provider;

    public void Dispose() => _provider?.Dispose();

    [Fact]
    public async Task R2020_StartupCatchUp_RollsUpEachHourOfTheWindowOnce_InItsOwnScope()
    {
        var service = CreateService();

        await service.SweepAsync(TestContext.Current.CancellationToken, catchUp: true);

        var expected = Enumerable.Range(0, 48).Select(i => CurrentHour.AddHours(-48 + i)).ToList();
        Assert.Equal(expected, AggregatedHours(_health));
        Assert.Equal(expected, AggregatedHours(_metrics));
        // One scope per aggregated hour, plus the one of the purges.
        Assert.Equal(48 + 1, _healthScopes);
    }

    [Fact]
    public async Task R2020_HourlySweep_RollsUpOnlyTheShortLookback_HourByHour()
    {
        var service = CreateService();

        await service.SweepAsync(TestContext.Current.CancellationToken, catchUp: false);

        var expected = Enumerable.Range(0, 6).Select(i => CurrentHour.AddHours(-6 + i)).ToList();
        Assert.Equal(expected, AggregatedHours(_health));
        Assert.Equal(expected, AggregatedHours(_metrics));
    }

    [Fact]
    public async Task Plan013Lot4_MetricDetailAggregatesAndLogs_FollowEachProjectsDataRetention()
    {
        // Apps 1 and 3 belong to Standard projects (7 days, 13 months, 14 days), app 2 to a Light one
        // (2 days, 3 months, 7 days).
        _retention.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new AppDataRetention(1, 10, DataRetentionProfiles.Standard),
            new AppDataRetention(2, 20, DataRetentionProfiles.Light),
            new AppDataRetention(3, 30, DataRetentionProfiles.Standard)
        ]);
        var service = CreateService();

        await service.SweepAsync(TestContext.Current.CancellationToken, catchUp: false);

        var now = CurrentHour.AddMinutes(6).AddSeconds(31);
        await _metrics.Received(1).PurgeRawOlderThanAsync(now.AddDays(-7), Ids(1, 3), Arg.Any<CancellationToken>());
        await _metrics.Received(1).PurgeRawOlderThanAsync(now.AddDays(-2), Ids(2), Arg.Any<CancellationToken>());
        await _metrics.Received(1).PurgeHourlyOlderThanAsync(now.AddMonths(-13), Ids(1, 3), Arg.Any<CancellationToken>());
        await _metrics.Received(1).PurgeHourlyOlderThanAsync(now.AddMonths(-3), Ids(2), Arg.Any<CancellationToken>());
        await _logs.Received(1).PurgeOlderThanAsync(now.AddDays(-14), Ids(1, 3), Arg.Any<CancellationToken>());
        await _logs.Received(1).PurgeOlderThanAsync(now.AddDays(-7), Ids(2), Arg.Any<CancellationToken>());
    }

    private static IReadOnlyCollection<int> Ids(params int[] ids) =>
        Arg.Is<IReadOnlyCollection<int>>(actual => actual.Order().SequenceEqual(ids));

    private AppTelemetryRetentionService CreateService()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            _healthScopes++;
            return _health;
        });
        services.AddScoped(_ => _metrics);
        services.AddScoped(_ => _logs);
        services.AddScoped(_ => _retention);
        services.AddScoped(_ => Substitute.For<IAppErrorRepository>());
        services.AddScoped(_ => Substitute.For<IAppVisitorRepository>());
        services.AddScoped(_ => Substitute.For<IAppWebAnalyticsRepository>());
        services.AddScoped(_ => Substitute.For<IAppWebAnalyticsService>());
        services.AddScoped(_ => Substitute.For<IAppWebAnalyticsConfigurationService>());
        _provider = services.BuildServiceProvider();
        var time = new FakeTimeProvider(new DateTimeOffset(CurrentHour.AddMinutes(6).AddSeconds(31)));
        return new AppTelemetryRetentionService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<AppTelemetryRetentionService>.Instance,
            time);
    }

    private static List<DateTime> AggregatedHours(object repository) =>
        repository.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == "AggregateHourAsync")
            .Select(call => (DateTime)call.GetArguments()[0]!)
            .ToList();
}
