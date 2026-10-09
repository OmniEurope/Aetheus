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

namespace Aetheus.Back.Tests.Tasks;

/// <summary>
/// An infrastructure outage suspends pipeline work instead of killing it.
///
/// Before this, an agent going offline mid-run meant every task it held aged out as Timeout and the
/// run failed, so a two-hour qualification was lost to a reboot. The work now returns to Pending
/// while the agent is confirmed offline and the agent picks it up when it reconnects.
///
/// The boundaries matter as much as the behaviour, and each has a case here: only pipeline work
/// parks, only for an agent actually offline, only within the grace, and never a deployment.
/// </summary>
public sealed class TaskTimeoutServiceOutageStandbyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static (TaskTimeoutService Sut, ITaskRepository Repo) BuildSut(
        TimeSpan? grace = null, IClientProxy? proxy = null)
    {
        var repo = Substitute.For<ITaskRepository>();
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetStaleAssignedTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetTasksFromSupersededAgentSessionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        repo.FindPipelineStepRunsByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var provider = new ServiceCollection()
            .AddScoped(_ => repo)
            .AddScoped(_ => Substitute.For<IPipelineRunService>())
            .BuildServiceProvider();

        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(proxy ?? Substitute.For<IClientProxy>());
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(clients);

        var options = Options.Create(new BackgroundServicesOptions());
        if (grace is { } value) options.Value.OfflineAgentGrace = value;

        var sut = new TaskTimeoutService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<TaskTimeoutService>>(),
            options,
            hub,
            new FakeTimeProvider(Now));

        return (sut, repo);
    }

    private static ServerTask Task(
        OperationKind operation = OperationKind.PipelineSubstituteVariables,
        int? runId = 42,
        TimeSpan? age = null) => new()
        {
            Id = 1,
            ServerId = 5,
            Name = "step",
            Status = TaskExecutionStatus.Running,
            AssignedAt = Now.UtcDateTime,
            StartedAt = Now.UtcDateTime,
            AssignedAgentSessionId = "session-a",
            PipelineRunId = runId,
            PipelineStepRunId = runId is null ? null : 9,
            Operation = operation,
            CreatedAt = Now.UtcDateTime - (age ?? TimeSpan.FromMinutes(30))
        };

    // Claimed (or queued) but never reported as started: the work an outage may replay.
    private static ServerTask NeverStarted(TaskExecutionStatus status, OperationKind operation = OperationKind.None,
        TimeSpan? age = null)
    {
        var task = Task(operation, age: age);
        task.Status = status;
        task.StartedAt = null;
        if (status == TaskExecutionStatus.Pending)
        {
            task.AssignedAt = null;
            task.AssignedAgentSessionId = null;
        }
        return task;
    }

    private static void Offline(ITaskRepository repo)
        => repo.GetServerStatusAsync(5, Arg.Any<CancellationToken>()).Returns(ServerStatus.Offline);

    // An agent that self-repairs (its heartbeat loop stalls, it terminates so the service manager
    // restarts it) comes back within seconds. The server keeps reporting Online while its polling
    // lease has lapsed, so it is neither offline nor claiming. Parking only the confirmed-Offline half
    // let that hiccup kill queued pipeline work: the run was told the task stays queued and resumes,
    // then failed minutes later because the Pending sweep ages a task from CreatedAt, which a
    // re-queued task never resets. A hiccup must cost time, not the run.
    [Fact]
    public async Task PendingPipelineTaskWhoseOnlineAgentStoppedClaiming_WaitsInsteadOfTimingOut()
    {
        var (sut, repo) = BuildSut();
        var task = NeverStarted(TaskExecutionStatus.Pending);
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        // Online, but its task loop is not claiming: exactly the restarting-agent window.
        repo.GetServerStatusAsync(5, Arg.Any<CancellationToken>()).Returns(ServerStatus.Online);
        repo.IsServerTaskPollingActiveAsync(5, Arg.Any<CancellationToken>()).Returns(false);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Pending, task.Status);
        Assert.Null(task.CompletedAt);
        Assert.Null(task.AssignedAt);
        Assert.Null(task.AssignedAgentSessionId);
    }

    // The grace still bounds the wait: parking is not "queued forever".
    [Fact]
    public async Task PendingPipelineTaskPastTheGrace_StillTimesOutEvenWhileTheAgentIsSilent()
    {
        var (sut, repo) = BuildSut(grace: TimeSpan.FromMinutes(10));
        var task = Task(age: TimeSpan.FromMinutes(45));
        task.Status = TaskExecutionStatus.Pending;
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        repo.GetServerStatusAsync(5, Arg.Any<CancellationToken>()).Returns(ServerStatus.Online);
        repo.IsServerTaskPollingActiveAsync(5, Arg.Any<CancellationToken>()).Returns(false);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
    }

    // A one-off server task has nobody waiting to resume it, so it still ages out.
    [Fact]
    public async Task PendingTaskWithoutAPipelineRun_StillTimesOutWhenItsAgentStopsClaiming()
    {
        var (sut, repo) = BuildSut();
        var task = Task(runId: null);
        task.Status = TaskExecutionStatus.Pending;
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        repo.GetServerStatusAsync(5, Arg.Any<CancellationToken>()).Returns(ServerStatus.Online);
        repo.IsServerTaskPollingActiveAsync(5, Arg.Any<CancellationToken>()).Returns(false);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
    }

    [Fact]
    public async Task RunningPipelineTaskOfAnOfflineAgent_IsNotReplayed_BecauseItAlreadyStarted()
    {
        // BACK-025 (user decision): a started command may already have written to a third party.
        var (sut, repo) = BuildSut();
        var task = Task(OperationKind.None);
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        Offline(repo);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task AssignedNeverStartedPipelineTaskOfAnOfflineAgent_GoesBackToTheQueueInsteadOfTimingOut()
    {
        var (sut, repo) = BuildSut();
        var task = NeverStarted(TaskExecutionStatus.Assigned);
        repo.GetStaleAssignedTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        Offline(repo);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Pending, task.Status);
        // The claim must start from scratch, otherwise the returning agent inherits a half-started task.
        Assert.Null(task.AssignedAt);
        Assert.Null(task.StartedAt);
        Assert.Null(task.AssignedAgentSessionId);
        Assert.Null(task.CompletedAt);
        await repo.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SameTaskWhileTheAgentIsOnline_StillTimesOut()
    {
        // The discriminating case: parking must be caused by the outage, not by the task being slow.
        var (sut, repo) = BuildSut();
        var task = Task();
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        repo.GetServerStatusAsync(5, Arg.Any<CancellationToken>()).Returns(ServerStatus.Online);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
    }

    [Theory]
    [InlineData(OperationKind.PipelineDeploy)]
    [InlineData(OperationKind.BlueGreenSwitch)]
    [InlineData(OperationKind.BlueGreenCommit)]
    [InlineData(OperationKind.BlueGreenRollback)]
    [InlineData(OperationKind.PipelineCreateRelease)]
    public async Task DeploymentWorkNeverParks_BecauseItCannotBeReplayedBlind(OperationKind operation)
    {
        // A cutover interrupted half-way has already changed the target. Replaying it could switch
        // traffic onto a stack whose migration never finished, so it fails closed and waits for a human.
        // Never started, so only the operation itself keeps it from replaying.
        var (sut, repo) = BuildSut();
        var task = NeverStarted(TaskExecutionStatus.Assigned, operation);
        repo.GetStaleAssignedTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        Offline(repo);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
    }

    [Fact]
    public async Task AOneOffServerTaskStillAgesOut_BecauseNobodyIsWaitingToResumeIt()
    {
        // The apache2-install regression must stay fixed: a task with no run behind it has no
        // resumption story, and leaving it queued forever is what made it invisible.
        var (sut, repo) = BuildSut();
        var task = Task(OperationKind.ApacheReload, runId: null);
        task.Status = TaskExecutionStatus.Pending;
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        repo.IsServerTaskPollingActiveAsync(5, Arg.Any<CancellationToken>()).Returns(false);
        Offline(repo);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
    }

    [Fact]
    public async Task PastTheGrace_TheOutageStopsBeingAnExcuseAndTheTaskFails()
    {
        var (sut, repo) = BuildSut(grace: TimeSpan.FromHours(1));
        var task = Task(age: TimeSpan.FromHours(3));
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        Offline(repo);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
    }

    [Fact]
    public async Task HostShutdown_NeverStartedWorkOfTheRestartedAgentSession_IsRequeuedNotFailed()
    {
        // The real incident (2026-08-19): the PC went down overnight mid-pipeline. The agent came
        // back with a NEW session, so its tasks arrived through the superseded-session sweep, not the
        // offline sweeps. Work the old session claimed but never started is redone by the new one.
        var (sut, repo) = BuildSut();
        var task = NeverStarted(TaskExecutionStatus.Assigned);
        repo.GetTasksFromSupersededAgentSessionsAsync(Arg.Any<CancellationToken>()).Returns([task]);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Pending, task.Status);
        Assert.Null(task.AssignedAgentSessionId);
        Assert.Null(task.AssignedAt);
    }

    [Fact]
    public async Task HostShutdown_StartedShellCommandOfTheReplacedSession_FailsWithItsReasonInsteadOfReplaying()
    {
        // BACK-025 (user decision): the shell step was Running when the session was replaced. Put back
        // to Pending, the new session would claim and execute it a second time, with a second real
        // effect. It fails, the run says why, and nothing is queued again.
        var (sut, repo) = BuildSut();
        var task = Task(OperationKind.None);
        var step = new PipelineStepRun { Id = 9, PipelineRunId = 42, StageName = "Publish", StepName = "push", Status = TaskExecutionStatus.Running };
        repo.GetTasksFromSupersededAgentSessionsAsync(Arg.Any<CancellationToken>()).Returns([task]);
        repo.FindPipelineStepRunsByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStepRun> { [9] = step });

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(TaskExecutionStatus.Pending, task.Status);
        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
        Assert.NotNull(task.StartedAt);
        Assert.Equal(PipelineOutageReplayPolicy.NotReplayedReason, task.FailureReason);
        Assert.Equal(TaskExecutionStatus.Timeout, step.Status);
        Assert.Equal(PipelineOutageReplayPolicy.NotReplayedReason, step.FailureReason);
    }

    [Fact]
    public async Task HostShutdown_DeploymentWorkOfTheOldSession_StillFailsClosed()
    {
        var (sut, repo) = BuildSut();
        var task = Task(OperationKind.BlueGreenSwitch);
        repo.GetTasksFromSupersededAgentSessionsAsync(Arg.Any<CancellationToken>()).Returns([task]);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
    }

    [Fact]
    public async Task HostShutdown_ARunOlderThanTheGrace_IsNotResurrected()
    {
        // A 539-minute outage resumes (9h < 24h); a task older than the grace does not: nobody wants
        // a qualification from three days ago silently completing under a newer one.
        var (sut, repo) = BuildSut(grace: TimeSpan.FromHours(1));
        var task = NeverStarted(TaskExecutionStatus.Assigned, age: TimeSpan.FromHours(9));
        repo.GetTasksFromSupersededAgentSessionsAsync(Arg.Any<CancellationToken>()).Returns([task]);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
    }

    [Fact]
    public async Task AParkedTaskIsNotAnnouncedAsCompleted()
    {
        // The run is still going. Broadcasting TaskCompleted would drop the chip from the top bar and
        // tell the user the work ended.
        // The proxy is the one the hub hands out, so a TaskCompleted broadcast would land on it.
        var proxy = Substitute.For<IClientProxy>();
        var (sut, repo) = BuildSut(proxy: proxy);
        var task = NeverStarted(TaskExecutionStatus.Assigned);
        repo.GetStaleAssignedTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([task]);
        Offline(repo);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Pending, task.Status);
        await proxy.DidNotReceive().SendCoreAsync(
            "TaskCompleted", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }
}
