// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelineSchedulerServiceTests
{
    private static (PipelineSchedulerService Sut, IPipelineRunService RunService) BuildSut(
        IPipelineRepository pipelineRepo, FakeTimeProvider clock)
    {
        var runService = Substitute.For<IPipelineRunService>();
        var services = new ServiceCollection();
        services.AddScoped<IPipelineAutoTriggerRepository>(_ => pipelineRepo);
        services.AddScoped(_ => runService);
        var sp = services.BuildServiceProvider();
        var sut = new PipelineSchedulerService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<PipelineSchedulerService>>(),
            new MemoryCache(new MemoryCacheOptions()),
            clock);
        return (sut, runService);
    }

    [Theory]
    [InlineData("schedule: '0 * * * * *'", "0 * * * * *")]
    [InlineData("schedule: \"0 0 * * * *\"", "0 0 * * * *")]
    [InlineData("schedule: 0 30 * * * *", "0 30 * * * *")]
    [InlineData("name: pipeline\nschedule: 0 0 6 * * *\nstages:", "0 0 6 * * *")]
    [InlineData("name: pipeline\nstages:", null)]
    [InlineData("", null)]
    public void ExtractSchedule_ParsesCorrectly(string yaml, string? expected)
    {
        var method = typeof(PipelineSchedulerService).GetMethod("ExtractSchedule",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = (string?)method.Invoke(null, [yaml]);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task CheckScheduledPipelines_TriggersDuePipeline()
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        // Fires at minute 0 of every hour.
        var pipeline = new Pipeline { Id = 7, Name = "hourly", YamlDefinition = "schedule: '0 0 * * * *'\nstages:\n  - name: build" };
        pipelineRepo.GetScheduledPipelinesAsync(Arg.Any<CancellationToken>()).Returns([pipeline]);
        pipelineRepo.GetPipelineIdsWithActiveRunsAsync(Arg.Any<CancellationToken>()).Returns([]);

        // Now is exactly the top of an hour, so the next occurrence falls inside the check window.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 14, 0, 0, TimeSpan.Zero));
        var (sut, runService) = BuildSut(pipelineRepo, clock);

        await sut.CheckScheduledPipelinesAsync(TestContext.Current.CancellationToken);

        await runService.Received(1).TriggerAutomatedRunAsync(7, "Scheduler", Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckScheduledPipelines_SkipsPipeline_WhenActiveRunExists()
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var pipeline = new Pipeline { Id = 2, Name = "busy", YamlDefinition = "schedule: '0 0 * * * *'\nstages:\n  - name: build" };
        pipelineRepo.GetScheduledPipelinesAsync(Arg.Any<CancellationToken>()).Returns([pipeline]);
        pipelineRepo.GetPipelineIdsWithActiveRunsAsync(Arg.Any<CancellationToken>()).Returns([2]);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 14, 0, 0, TimeSpan.Zero));
        var (sut, runService) = BuildSut(pipelineRepo, clock);

        await sut.CheckScheduledPipelinesAsync(TestContext.Current.CancellationToken);

        await runService.DidNotReceive().TriggerAutomatedRunAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckScheduledPipelines_SkipsPipeline_WhenNotDue()
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        // Fires at minute 0; clock is at minute 30 - outside the 1-minute window.
        var pipeline = new Pipeline { Id = 5, Name = "hourly", YamlDefinition = "schedule: '0 0 * * * *'\nstages:\n  - name: build" };
        pipelineRepo.GetScheduledPipelinesAsync(Arg.Any<CancellationToken>()).Returns([pipeline]);
        pipelineRepo.GetPipelineIdsWithActiveRunsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 14, 30, 0, TimeSpan.Zero));
        var (sut, runService) = BuildSut(pipelineRepo, clock);

        await sut.CheckScheduledPipelinesAsync(TestContext.Current.CancellationToken);

        await runService.DidNotReceive().TriggerAutomatedRunAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckScheduledPipelines_SkipsPipeline_WhenNoSchedule()
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var pipeline = new Pipeline { Id = 3, Name = "manual", YamlDefinition = "name: test\nstages:\n  - name: build" };
        pipelineRepo.GetScheduledPipelinesAsync(Arg.Any<CancellationToken>()).Returns([pipeline]);
        pipelineRepo.GetPipelineIdsWithActiveRunsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 14, 0, 0, TimeSpan.Zero));
        var (sut, runService) = BuildSut(pipelineRepo, clock);

        await sut.CheckScheduledPipelinesAsync(TestContext.Current.CancellationToken);

        await runService.DidNotReceive().TriggerAutomatedRunAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckScheduledPipelines_HandlesInvalidCron_WithoutTriggering()
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var pipeline = new Pipeline { Id = 4, Name = "bad-cron", YamlDefinition = "schedule: 'not-a-cron'\nstages:\n  - name: build" };
        pipelineRepo.GetScheduledPipelinesAsync(Arg.Any<CancellationToken>()).Returns([pipeline]);
        pipelineRepo.GetPipelineIdsWithActiveRunsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 14, 0, 0, TimeSpan.Zero));
        var (sut, runService) = BuildSut(pipelineRepo, clock);

        await sut.CheckScheduledPipelinesAsync(TestContext.Current.CancellationToken);

        await runService.DidNotReceive().TriggerAutomatedRunAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckScheduledPipelines_ConfigurationRefusal_DoesNotAbortFollowingPipeline()
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var rejected = new Pipeline { Id = 8, Name = "rejected", YamlDefinition = "schedule: '0 0 * * * *'\nstages:\n  - name: build" };
        var valid = new Pipeline { Id = 9, Name = "valid", YamlDefinition = "schedule: '0 0 * * * *'\nstages:\n  - name: build" };
        pipelineRepo.GetScheduledPipelinesAsync(Arg.Any<CancellationToken>()).Returns([rejected, valid]);
        pipelineRepo.GetPipelineIdsWithActiveRunsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 14, 0, 0, TimeSpan.Zero));
        var (sut, runService) = BuildSut(pipelineRepo, clock);
        runService.TriggerAutomatedRunAsync(8, "Scheduler", Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<PipelineRunDto?>(
                new BadRequestException("invalid matrix")));

        await sut.CheckScheduledPipelinesAsync(TestContext.Current.CancellationToken);

        await runService.Received(1).TriggerAutomatedRunAsync(
            9, "Scheduler", Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }
}
