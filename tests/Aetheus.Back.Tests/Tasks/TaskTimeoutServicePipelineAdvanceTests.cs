// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.Tasks;

/// <summary>
/// What happens to the pipeline RUN when a task dies, as opposed to what happens to the task.
///
/// The task-level sweeps were covered; everything past them was not. TaskTimeoutServiceTests never
/// mentioned AdvanceStageAsync, ContinueAfterArtifactCollectionAsync or IPipelineRunService, so the
/// whole "an agent died mid-pipeline" path - the one an outage actually takes - had no unit test at
/// all. These cases pin it.
/// </summary>
public sealed class TaskTimeoutServicePipelineAdvanceTests
{
    private static (TaskTimeoutService Sut, ITaskRepository Repo, IPipelineRunService Runs) BuildSut(
        FakeTimeProvider clock)
    {
        var repo = Substitute.For<ITaskRepository>();
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetStaleAssignedTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetTasksFromSupersededAgentSessionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        repo.FindPipelineStepRunsByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var runs = Substitute.For<IPipelineRunService>();
        var provider = new ServiceCollection()
            .AddScoped(_ => repo)
            .AddScoped(_ => runs)
            .BuildServiceProvider();

        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(clients);

        var sut = new TaskTimeoutService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<TaskTimeoutService>>(),
            Options.Create(new BackgroundServicesOptions()),
            hub,
            clock,
            leaderLease: LeaderLeaseDouble.Leader());

        return (sut, repo, runs);
    }

    private static ServerTask PipelineTask(int id, int runId, int? stepRunId, OperationKind operation) => new()
    {
        Id = id,
        ServerId = 1,
        Name = $"task-{id}",
        Status = TaskExecutionStatus.Running,
        PipelineRunId = runId,
        PipelineStepRunId = stepRunId,
        Operation = operation
    };

    [Fact]
    public async Task RunningTaskOfAStep_TimesOutTheStepAndAdvancesItsStage()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));
        var (sut, repo, runs) = BuildSut(clock);

        var task = PipelineTask(11, runId: 42, stepRunId: 5, OperationKind.PipelineSubstituteVariables);
        var stepRun = new PipelineStepRun { Id = 5, StageName = "CI", Status = TaskExecutionStatus.Running };
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        repo.FindPipelineStepRunsByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStepRun> { [5] = stepRun });

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, stepRun.Status);
        Assert.Equal(new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc), stepRun.CompletedAt);
        await runs.Received(1).AdvanceStageAsync(42, "CI", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StepAlreadyTerminal_IsNotReopenedAndItsStageIsNotAdvancedAgain()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));
        var (sut, repo, runs) = BuildSut(clock);

        // The step's own completion won the race with the sweep. Advancing again would push the run
        // forward a second time on the same stage.
        var task = PipelineTask(12, runId: 43, stepRunId: 6, OperationKind.PipelineSubstituteVariables);
        var settled = new PipelineStepRun { Id = 6, StageName = "CI", Status = TaskExecutionStatus.Success };
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        repo.FindPipelineStepRunsByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStepRun> { [6] = settled });

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Success, settled.Status);
        await runs.DidNotReceive().AdvanceStageAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ArtifactCollectionTaskWithoutAStep_SettlesTheRunInsteadOfHangingIt()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));
        var (sut, repo, runs) = BuildSut(clock);

        // S-TECH-ARCR: a post-stage collection task carries a run id but NO step run, so the advance
        // loop above never sees it. Without this path the run waits for a collection that is dead.
        var task = PipelineTask(13, runId: 44, stepRunId: null, OperationKind.PipelineCollectArtifacts);
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        await runs.Received(1).ContinueAfterArtifactCollectionAsync(
            44, TaskExecutionStatus.Timeout, Arg.Any<CancellationToken>());
        await runs.DidNotReceive().AdvanceStageAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneRunThatThrowsWhileAdvancing_DoesNotStopTheOtherRunsOfTheSameSweep()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));
        var (sut, repo, runs) = BuildSut(clock);

        var first = PipelineTask(14, runId: 45, stepRunId: 7, OperationKind.PipelineSubstituteVariables);
        var second = PipelineTask(15, runId: 46, stepRunId: 8, OperationKind.PipelineSubstituteVariables);
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([first, second]);
        repo.FindPipelineStepRunsByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStepRun>
            {
                [7] = new() { Id = 7, StageName = "CI", Status = TaskExecutionStatus.Running },
                [8] = new() { Id = 8, StageName = "QA", Status = TaskExecutionStatus.Running }
            });
        runs.AdvanceStageAsync(45, "CI", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the run blew up"));

        // The sweep must survive it: a background service that rethrows here stops recovering every
        // other wedged run until the next process restart.
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        await runs.Received(1).AdvanceStageAsync(46, "QA", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationDuringAdvance_PropagatesInsteadOfBeingSwallowed()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));
        var (sut, repo, runs) = BuildSut(clock);
        using var cancellation = new CancellationTokenSource();

        var task = PipelineTask(16, runId: 47, stepRunId: 9, OperationKind.PipelineSubstituteVariables);
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        repo.FindPipelineStepRunsByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStepRun>
            {
                [9] = new() { Id = 9, StageName = "CI", Status = TaskExecutionStatus.Running }
            });
        runs.AdvanceStageAsync(47, "CI", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        // Shutdown is not a failure to log and continue: it must reach the hosted-service loop.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.CheckStaleTasksAsync(cancellation.Token));
    }
}
