// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Data;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Artifacts;

public sealed class ArtifactStorageMonitorServiceTests
{
    [Fact]
    public async Task EvaluateAsync_WhenPhysicalBudgetExceeded_SendsCriticalAlert()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-artifact-monitor-");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "artifact.bin"), new byte[32], cancellationToken: TestContext.Current.CancellationToken);
            var (hub, proxy) = AlertHub();
            var service = new ArtifactStorageMonitorService(
                Configuration(root.FullName, budgetBytes: 16, growthWarningBytesPerDay: 0),
                hub, Measurements(), TimeProvider.System, NullLogger<ArtifactStorageMonitorService>.Instance,
                leaderLease: LeaderLeaseDouble.Leader());
            AlertTriggeredDto? alert = null;
            proxy.SendCoreAsync("AlertTriggered", Arg.Do<object?[]>(arguments =>
                    alert = Assert.IsType<AlertTriggeredDto>(arguments[0])), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            await service.EvaluateAsync(TestContext.Current.CancellationToken);

            await proxy.Received(1).SendCoreAsync(
                "AlertTriggered", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
            Assert.NotNull(alert);
            Assert.Equal("ArtifactStorage", alert.RuleName);
            Assert.Equal("ArtifactVolumeBytes", alert.Metric);
            Assert.Equal("Critical", alert.Severity);
            Assert.Equal(16, alert.Threshold);
            Assert.Equal(32, ArtifactStorageMonitorService.MeasureDirectoryBytes(root.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task R2016_AOneHourSpike_IsNotProjectedToADailyGrowth()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-artifact-spike-");
        try
        {
            var artifact = Path.Combine(root.FullName, "artifact.bin");
            await File.WriteAllBytesAsync(artifact, new byte[100], cancellationToken: TestContext.Current.CancellationToken);
            var (hub, proxy) = AlertHub();
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
            var service = new ArtifactStorageMonitorService(
                Configuration(root.FullName, budgetBytes: 500, growthWarningBytesPerDay: 50),
                hub, Measurements(), time, NullLogger<ArtifactStorageMonitorService>.Instance,
                leaderLease: LeaderLeaseDouble.Leader());

            await service.EvaluateAsync(TestContext.Current.CancellationToken);
            // +20 bytes in one hour: the former normalisation (x24) read 480 bytes per day against 50.
            await File.WriteAllBytesAsync(artifact, new byte[120], cancellationToken: TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromHours(1));
            await service.EvaluateAsync(TestContext.Current.CancellationToken);

            await proxy.DidNotReceive().SendCoreAsync(
                "AlertTriggered", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task R2016_FastGrowthOverADay_WarnsWhenTheBudgetIsNear_WithASixHourCooldown()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-artifact-growth-");
        try
        {
            var artifact = Path.Combine(root.FullName, "artifact.bin");
            await File.WriteAllBytesAsync(artifact, [], cancellationToken: TestContext.Current.CancellationToken);
            var (hub, proxy) = AlertHub();
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero));
            var service = new ArtifactStorageMonitorService(
                Configuration(root.FullName, budgetBytes: 1000, growthWarningBytesPerDay: 100),
                hub, Measurements(), time, NullLogger<ArtifactStorageMonitorService>.Instance,
                leaderLease: LeaderLeaseDouble.Leader());

            await service.EvaluateAsync(TestContext.Current.CancellationToken);
            await proxy.DidNotReceive().SendCoreAsync(
                "AlertTriggered", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());

            // 200 bytes in 24 hours: over the 100 per day threshold, budget reached in 4 days.
            await File.WriteAllBytesAsync(artifact, new byte[200], cancellationToken: TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromHours(24));
            await service.EvaluateAsync(TestContext.Current.CancellationToken);
            await proxy.Received(1).SendCoreAsync(
                "AlertTriggered",
                Arg.Is<object?[]>(arguments =>
                    ((AlertTriggeredDto)arguments[0]!).Severity == "Warning"
                    && ((AlertTriggeredDto)arguments[0]!).Threshold == 100),
                Arg.Any<CancellationToken>());

            await File.WriteAllBytesAsync(artifact, new byte[220], cancellationToken: TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromHours(1));
            await service.EvaluateAsync(TestContext.Current.CancellationToken);
            await proxy.Received(1).SendCoreAsync(
                "AlertTriggered", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task R2016_FastGrowthFarFromTheBudget_DoesNotWarn()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-artifact-far-");
        try
        {
            var artifact = Path.Combine(root.FullName, "artifact.bin");
            await File.WriteAllBytesAsync(artifact, [], cancellationToken: TestContext.Current.CancellationToken);
            var (hub, proxy) = AlertHub();
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero));
            var service = new ArtifactStorageMonitorService(
                Configuration(root.FullName, budgetBytes: 100_000, growthWarningBytesPerDay: 100),
                hub, Measurements(), time, NullLogger<ArtifactStorageMonitorService>.Instance,
                leaderLease: LeaderLeaseDouble.Leader());

            await service.EvaluateAsync(TestContext.Current.CancellationToken);
            // 200 bytes per day, budget reached in about 500 days: no real risk.
            await File.WriteAllBytesAsync(artifact, new byte[200], cancellationToken: TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromHours(24));
            await service.EvaluateAsync(TestContext.Current.CancellationToken);

            await proxy.DidNotReceive().SendCoreAsync(
                "AlertTriggered", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void R2016_Growth_IsMeasuredAgainstTheNewestMeasurementAtLeastADayOld()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        List<(DateTime At, long Bytes)> history =
        [
            (now.AddHours(-30), 1_000),
            (now.AddHours(-24), 4_000),
            (now.AddHours(-1), 9_000)
        ];

        Assert.Null(ArtifactStorageMonitorService.GrowthBytesPerDay([(now.AddHours(-23), 0)], now, 5_000));
        Assert.Equal(6_000, ArtifactStorageMonitorService.GrowthBytesPerDay(history, now, 10_000));
        Assert.Equal(0, ArtifactStorageMonitorService.GrowthBytesPerDay(history, now, 3_000));
        Assert.Equal(7.5, ArtifactStorageMonitorService.DaysUntilBudget(10_000, 55_000, 6_000));
        Assert.Null(ArtifactStorageMonitorService.DaysUntilBudget(10_000, 0, 6_000));
        Assert.Null(ArtifactStorageMonitorService.DaysUntilBudget(10_000, 55_000, 0));
    }

    [Fact]
    public async Task R2016_AfterARestart_TheGrowthIsMeasuredAgainstThePersistedMeasurement()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-artifact-restart-");
        try
        {
            var artifact = Path.Combine(root.FullName, "artifact.bin");
            await File.WriteAllBytesAsync(artifact, [], cancellationToken: TestContext.Current.CancellationToken);
            var (hub, proxy) = AlertHub();
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
            var database = Measurements();
            var configuration = Configuration(root.FullName, budgetBytes: 1000, growthWarningBytesPerDay: 100);

            await new ArtifactStorageMonitorService(configuration, hub, database, time,
                NullLogger<ArtifactStorageMonitorService>.Instance,
                leaderLease: LeaderLeaseDouble.Leader()).EvaluateAsync(TestContext.Current.CancellationToken);

            // A deploy every few hours: each new process measures once, none of them holds a day of
            // history in memory, yet the fourth one sees the growth of the last 24 hours.
            for (var deploy = 0; deploy < 3; deploy++)
            {
                time.Advance(TimeSpan.FromHours(8));
                await File.WriteAllBytesAsync(artifact, new byte[70 * (deploy + 1)], cancellationToken: TestContext.Current.CancellationToken);
                await new ArtifactStorageMonitorService(configuration, hub, database, time,
                    NullLogger<ArtifactStorageMonitorService>.Instance,
                    leaderLease: LeaderLeaseDouble.Leader()).EvaluateAsync(TestContext.Current.CancellationToken);
            }

            await proxy.Received(1).SendCoreAsync(
                "AlertTriggered",
                Arg.Is<object?[]>(arguments =>
                    ((AlertTriggeredDto)arguments[0]!).Severity == "Warning"
                    && ((AlertTriggeredDto)arguments[0]!).Threshold == 100),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task R2016_PersistedHistory_KeepsTheNewestMeasurementADayOldAndWhatFollowsIt()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-artifact-prune-");
        try
        {
            var (hub, _) = AlertHub();
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
            var database = Measurements();
            var configuration = Configuration(root.FullName, budgetBytes: 0, growthWarningBytesPerDay: 0);
            var start = time.GetUtcNow().UtcDateTime;

            for (var hour = 0; hour <= 30; hour += 6)
            {
                await new ArtifactStorageMonitorService(configuration, hub, database, time,
                    NullLogger<ArtifactStorageMonitorService>.Instance,
                    leaderLease: LeaderLeaseDouble.Leader()).EvaluateAsync(TestContext.Current.CancellationToken);
                time.Advance(TimeSpan.FromHours(6));
            }

            await using var scope = database.CreateAsyncScope();
            var kept = await scope.ServiceProvider.GetRequiredService<IArtifactStorageMeasurementRepository>()
                .GetAsync(TestContext.Current.CancellationToken);
            // Measured at 0, 6, 12, 18, 24 and 30 h: at 30 h the newest measurement 24 h old is the one of
            // 6 h, so only the one of 0 h is gone.
            Assert.Equal([6, 12, 18, 24, 30], kept.Select(item => (int)(item.At - start).TotalHours));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>A scope factory over one in-memory database, shared by every service built from it the
    /// way the blue-green colours and successive deploys share the production database.</summary>
    private static IServiceScopeFactory Measurements()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddScoped<IArtifactStorageMeasurementRepository, ArtifactStorageMeasurementRepository>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static IConfiguration Configuration(string basePath, long budgetBytes, long growthWarningBytesPerDay) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ArtifactStorage:BasePath"] = basePath,
                ["ArtifactStorage:VolumeBudgetBytes"] = budgetBytes.ToString(CultureInfo.InvariantCulture),
                ["ArtifactStorage:GrowthWarningBytesPerDay"] = growthWarningBytesPerDay.ToString(CultureInfo.InvariantCulture)
            }).Build();

    private static (IHubContext<AlertHub> Hub, IClientProxy Proxy) AlertHub()
    {
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(proxy);
        var hub = Substitute.For<IHubContext<AlertHub>>();
        hub.Clients.Returns(clients);
        return (hub, proxy);
    }
}
