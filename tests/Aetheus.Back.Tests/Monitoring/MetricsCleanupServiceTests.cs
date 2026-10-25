// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class MetricsCleanupServiceTests
{
    private static (MetricsCleanupService Sut, IServerHeartbeatRepository Repo) BuildSut(
        IConfiguration config, FakeTimeProvider clock)
    {
        var repo = Substitute.For<IServerHeartbeatRepository>();
        var sp = new ServiceCollection().AddScoped(_ => repo).BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var logger = Substitute.For<ILogger<MetricsCleanupService>>();
        return (new MetricsCleanupService(scopeFactory, config, logger, clock), repo);
    }

    [Fact]
    public async Task CleanupExpiredMetricsAsync_DeletesWithConfiguredRetentionCutoff()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Metrics:RetentionDays"] = "7" })
            .Build();
        var (sut, repo) = BuildSut(config, clock);
        repo.DeleteMetricsOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(42);

        await sut.CleanupExpiredMetricsAsync(TestContext.Current.CancellationToken);

        var expectedCutoff = new DateTime(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc);
        await repo.Received(1).DeleteMetricsOlderThanAsync(expectedCutoff, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupExpiredMetricsAsync_DefaultRetention_Uses30DayCutoff()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero));
        var (sut, repo) = BuildSut(new ConfigurationBuilder().Build(), clock);
        repo.DeleteMetricsOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);

        await sut.CleanupExpiredMetricsAsync(TestContext.Current.CancellationToken);

        var expectedCutoff = new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc);
        await repo.Received(1).DeleteMetricsOlderThanAsync(expectedCutoff, Arg.Any<CancellationToken>());
    }
}
