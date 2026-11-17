// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
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
        // The cursor update is a conditional write whose answer says "this instance took the occurrence".
        pipelineRepo.TryAdvanceScheduleCursorAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var runService = Substitute.For<IPipelineRunService>();
        var services = new ServiceCollection();
        services.AddScoped<IPipelineAutoTriggerRepository>(_ => pipelineRepo);
        services.AddScoped(_ => runService);
        var sp = services.BuildServiceProvider();
        var sut = new PipelineSchedulerService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<PipelineSchedulerService>>(),
            clock,
            leaderLease: LeaderLeaseDouble.Leader());
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
    public async Task R30_CheckScheduledPipelines_AnUnexpectedFailureOfOnePipeline_DoesNotStopTheFollowingOnes()
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var broken = new Pipeline { Id = 8, Name = "broken", YamlDefinition = "schedule: '0 0 * * * *'\nstages:\n  - name: build" };
        var valid = new Pipeline { Id = 9, Name = "valid", YamlDefinition = "schedule: '0 0 * * * *'\nstages:\n  - name: build" };
        pipelineRepo.GetScheduledPipelinesAsync(Arg.Any<CancellationToken>()).Returns([broken, valid]);
        pipelineRepo.GetPipelineIdsWithActiveRunsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 14, 0, 0, TimeSpan.Zero));
        var (sut, runService) = BuildSut(pipelineRepo, clock);
        runService.TriggerAutomatedRunAsync(8, "Scheduler", Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<PipelineRunDto?>(new InvalidOperationException("git unreachable")));

        await sut.CheckScheduledPipelinesAsync(TestContext.Current.CancellationToken);

        await runService.Received(1).TriggerAutomatedRunAsync(
            9, "Scheduler", Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
        // The failed pipeline's occurrence is given back, so the next tick still owes it.
        await pipelineRepo.Received(1).RestoreScheduleCursorAsync(
            8, Arg.Any<DateTime>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R30_CheckScheduledPipelines_OccurrencesTooOldToCatchUp_AreRecordedAsARefusedRun_NotJustLogged()
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 14, 0, 0, TimeSpan.Zero));
        // The scheduler was cut for five hours: 09:00 to 12:00 are older than the two-hour catch-up limit.
        var pipeline = new Pipeline
        {
            Id = 7,
            Name = "aetheus-nightly",
            YamlDefinition = "schedule: '0 0 * * * *'\nstages:\n  - name: build",
            LastScheduledOccurrence = new DateTime(2026, 6, 16, 8, 0, 0, DateTimeKind.Utc)
        };
        pipelineRepo.GetScheduledPipelinesAsync(Arg.Any<CancellationToken>()).Returns([pipeline]);
        pipelineRepo.GetPipelineIdsWithActiveRunsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var (sut, runService) = BuildSut(pipelineRepo, clock);

        await sut.CheckScheduledPipelinesAsync(TestContext.Current.CancellationToken);

        await runService.Received(1).RecordRefusedAutomatedLaunchAsync(
            7, "Scheduler",
            Arg.Is<string>(reason => reason.Contains("4 scheduled occurrence", StringComparison.Ordinal)
                && reason.Contains("not run", StringComparison.Ordinal)),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyList<ProblemDto>?>(problems => problems != null && problems.Count == 1
                && problems[0].Code == PipelineProblemCodes.ScheduleOccurrencesMissed
                && problems[0].Parameters["count"] == "4"
                && problems[0].Parameters["first"] == "2026-06-16 09:00"
                && problems[0].Parameters["last"] == "2026-06-16 12:00"
                && problems[0].Parameters["limit"] == "2"));
    }

    [Fact]
    public async Task CheckScheduledPipelines_OneDroppedOccurrence_IsRecordedWithTheSingularCode_AsTheRecorderStoresIt()
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 14, 0, 0, TimeSpan.Zero));
        var pipeline = new Pipeline
        {
            Id = 7,
            Name = "aetheus-nightly",
            YamlDefinition = "schedule: '0 0 * * * *'\nstages:\n  - name: build",
            LastScheduledOccurrence = new DateTime(2026, 6, 16, 11, 0, 0, DateTimeKind.Utc)
        };
        pipelineRepo.GetScheduledPipelinesAsync(Arg.Any<CancellationToken>()).Returns([pipeline]);
        pipelineRepo.GetPipelineIdsWithActiveRunsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var (sut, runService) = BuildSut(pipelineRepo, clock);

        await sut.CheckScheduledPipelinesAsync(TestContext.Current.CancellationToken);

        var call = runService.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IPipelineRunService.RecordRefusedAutomatedLaunchAsync));
        var args = call.GetArguments();
        var problems = Assert.IsAssignableFrom<IReadOnlyList<ProblemDto>>(args[5]);
        var problem = Assert.Single(problems);
        Assert.Equal(PipelineProblemCodes.ScheduleOccurrenceMissed, problem.Code);
        Assert.Equal("1", problem.Parameters["count"]);
        // The recorder wraps it as the inner problem of the stored refusal, which the front says in the user's language.
        var stored = PipelineRefusedLaunchRecorder.BuildReason("Scheduler", (string)args[2]!, problems);
        Assert.True(StoredProblem.TryRead(stored, out var content));
        Assert.Equal(PipelineProblemCodes.LaunchRefused, content.Problem.Code);
        Assert.Equal(PipelineProblemCodes.ScheduleOccurrenceMissed, Assert.Single(content.Inner).Code);
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
