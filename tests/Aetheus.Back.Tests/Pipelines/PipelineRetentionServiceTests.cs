// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

public class PipelineRetentionServiceTests
{
    private static (PipelineRetentionService Sut, IPipelineRunRetentionRepository PipelineRepo, ILogRepository LogRepo, ITaskRepository TaskRepo) BuildSut(
        IConfiguration config, FakeTimeProvider clock)
    {
        var pipelineRepo = Substitute.For<IPipelineRunRetentionRepository>();
        var logRepo = Substitute.For<ILogRepository>();
        var taskRepo = Substitute.For<ITaskRepository>();
        var sp = new ServiceCollection()
            .AddScoped(_ => pipelineRepo)
            .AddScoped(_ => logRepo)
            .AddScoped(_ => taskRepo)
            .BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var logger = Substitute.For<ILogger<PipelineRetentionService>>();
        return (new PipelineRetentionService(scopeFactory, config, logger, clock), pipelineRepo, logRepo, taskRepo);
    }

    [Fact]
    public async Task PruneOldRunsAsync_DeletesWithConfiguredCutoffs()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:PipelineRunDays"] = "30",
                ["Retention:LogDays"] = "7"
            })
            .Build();
        var (sut, pipelineRepo, logRepo, taskRepo) = BuildSut(config, clock);
        pipelineRepo.DeleteRunsOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(5);
        logRepo.DeleteLogsOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(3);

        await sut.PruneOldRunsAsync(TestContext.Current.CancellationToken);

        var expectedRunCutoff = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
        var expectedLogCutoff = new DateTime(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc);
        await pipelineRepo.Received(1).DeleteRunsOlderThanAsync(expectedRunCutoff, Arg.Any<CancellationToken>());
        await logRepo.Received(1).DeleteLogsOlderThanAsync(expectedLogCutoff, Arg.Any<CancellationToken>());
        await taskRepo.Received(1).DeleteCompletedTasksOlderThanAsync(
            new DateTime(2026, 3, 18, 12, 0, 0, DateTimeKind.Utc),
            expectedRunCutoff,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneOldRunsAsync_DefaultRetention_Uses90And30DayCutoffs()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero));
        var (sut, pipelineRepo, logRepo, taskRepo) = BuildSut(new ConfigurationBuilder().Build(), clock);

        await sut.PruneOldRunsAsync(TestContext.Current.CancellationToken);

        await pipelineRepo.Received(1).DeleteRunsOlderThanAsync(
            new DateTime(2026, 3, 18, 0, 0, 0, DateTimeKind.Utc), Arg.Any<CancellationToken>());
        await logRepo.Received(1).DeleteLogsOlderThanAsync(
            new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc), Arg.Any<CancellationToken>());
        await taskRepo.Received(1).DeleteCompletedTasksOlderThanAsync(
            new DateTime(2026, 3, 18, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 18, 0, 0, 0, DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }
}
