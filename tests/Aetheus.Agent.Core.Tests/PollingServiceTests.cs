// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Agent.Core.Tests;

public class PollingServiceTests
{
    private readonly IServerApiClient _apiClientMock = Substitute.For<IServerApiClient>();
    private readonly IExecutor _executorMock = Substitute.For<IExecutor>();
    private readonly IAgentProcessRestarter _processRestarter = Substitute.For<IAgentProcessRestarter>();
    private readonly IDeploymentBuildRefusalOutbox _refusalOutbox =
        Substitute.For<IDeploymentBuildRefusalOutbox>();
    private readonly AgentState _agentState;
    private readonly EnrollmentService _enrollment;
    private readonly IOptions<AetheusAgentOptions> _options;

    public PollingServiceTests()
    {
        _agentState = new AgentState
        {
            ServerId = 1,
            BearerToken = "test-token"
        };

        _options = Options.Create(new AetheusAgentOptions
        {
            ServerUrl = "http://localhost:5301",
            PollingIntervalSeconds = 1,
            MaxConcurrentTasks = 2
        });

        _enrollment = CreateEnrolledEnrollmentService();
        _refusalOutbox.ReadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DeploymentBuildRefusalReport>());

        _executorMock.Type.Returns(ExecutorType.Shell);
    }

    /// <summary>
    /// Makes the build lease immediately available on a maintenance double. Without it the substitute
    /// returns false and the poller correctly hands the task back to the queue, which is the subject of
    /// its own test rather than of the ones that assert on execution.
    /// </summary>
    private static void GrantBuildLease(IDockerStorageMaintenance maintenance) =>
        maintenance.PrepareBuildAsync(
                Arg.Any<IDictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>(),
                Arg.Any<TimeSpan?>())
            .Returns(true);

    // E-3: drive one poll iteration and await the dispatched in-flight tasks so the
    // assertion on a task's terminal effect is deterministic (no PeriodicTimer / sleep).
    private static async Task PollAndDrainAsync(PollingService service)
    {
        await service.PollOnceAsync(TestContext.Current.CancellationToken);
        await service.DrainRunningTasksAsync();
    }

    private readonly string _functionsDirectory =
        Path.Combine(Path.GetTempPath(), $"aetheus-fn-polling-{Guid.NewGuid():N}");

    private ContainerPipelineFunctionExecutor ContainerFunctions(IContainerExecutor container) =>
        new(PipelineFunctions.CreateAll(), container, _functionsDirectory, PipelineFunctionCatalog.Find);

    private PollingService CreateService(
        IEnumerable<IOperationExecutor>? operationExecutors = null,
        IDockerStorageMaintenance? dockerStorageMaintenance = null,
        AgentRuntimeHealth? runtimeHealth = null,
        TimeProvider? timeProvider = null,
        IContainerExecutor? containerExecutor = null,
        IOptions<AetheusAgentOptions>? options = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var container = containerExecutor ?? Substitute.For<IContainerExecutor>();
        return new(
        _apiClientMock,
        _enrollment,
        [_executorMock],
        operationExecutors ?? Array.Empty<IOperationExecutor>(),
        container,
        ContainerFunctions(container),
        dockerStorageMaintenance ?? Substitute.For<IDockerStorageMaintenance>(),
        _refusalOutbox,
        _agentState,
        runtimeHealth ?? new AgentRuntimeHealth(time),
        _processRestarter,
        time,
        options ?? _options,
        NullLogger<PollingService>.Instance);
    }

    [Fact]
    public async Task PollOnceAsync_PollsForTasksWhenEnrolled()
    {
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto>());

        var service = CreateService();
        await PollAndDrainAsync(service);

        await _apiClientMock.Received(1).GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_ApiFailure_DoesNotRefreshWatchdogSuccess()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero));
        var health = new AgentRuntimeHealth(time);
        health.MarkPollingSuccess();
        var previousSuccess = health.LastPollingSuccessAt;
        time.Advance(TimeSpan.FromMinutes(1));
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        var service = CreateService(runtimeHealth: health, timeProvider: time);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.PollOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(previousSuccess, health.LastPollingSuccessAt);
    }

    [Fact]
    public async Task PollOnceAsync_ApiSuccess_RefreshesWatchdogSuccess()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero));
        var health = new AgentRuntimeHealth(time);
        health.MarkPollingSuccess();
        time.Advance(TimeSpan.FromMinutes(1));
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto>());
        var service = CreateService(runtimeHealth: health, timeProvider: time);

        await service.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(time.GetUtcNow(), health.LastPollingSuccessAt);
    }

    [Fact]
    public void NextPollDelay_IdleAgentKeepsTheConfiguredInterval()
    {
        var service = CreateService(
            timeProvider: new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero)),
            options: TenSecondPolling);

        Assert.Equal(TimeSpan.FromSeconds(10), service.NextPollDelay());
    }

    [Fact]
    public async Task NextPollDelay_PollsFastWhileARunIsActiveThenFallsBackAfterTheWindow()
    {
        // Poll promptly after completion so newly created next-stage tasks do not wait a full
        // interval.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero));
        var gate = new TaskCompletionSource();
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new PendingTaskDto { Id = 7, Name = "stage", Command = "echo", Executor = ExecutorType.Shell }], []);
        _executorMock.ExecuteAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await gate.Task; return new ExecutorResult(0, false); });
        var service = CreateService(timeProvider: time, options: TenSecondPolling);

        await service.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentRuntimeDefaults.ActivePollingInterval, service.NextPollDelay());

        gate.SetResult();
        await service.DrainRunningTasksAsync();
        time.Advance(AgentRuntimeDefaults.ActivePollingWindow - TimeSpan.FromSeconds(1));
        Assert.Equal(AgentRuntimeDefaults.ActivePollingInterval, service.NextPollDelay());

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(10), service.NextPollDelay());
    }

    private static readonly IOptions<AetheusAgentOptions> TenSecondPolling = Options.Create(new AetheusAgentOptions
    {
        ServerUrl = "http://localhost:5301",
        PollingIntervalSeconds = 10,
        MaxConcurrentTasks = 2
    });

    [Fact]
    public async Task SendLogBatchAsync_CancelsAnUnresponsiveBackendWithinItsOwnBudget()
    {
        _apiClientMock.AppendLogBatchAsync(Arg.Any<List<AppendLogRequest>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PollingService.SendLogBatchAsync(
                _apiClientMock,
                [new AppendLogRequest { TaskId = 42, Message = "bounded" }],
                TestContext.Current.CancellationToken,
                TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public async Task PollOnceAsync_ExecutesReceivedTask()
    {
        var pendingTask = new PendingTaskDto
        {
            Id = 42,
            Name = "test-task",
            Command = "echo hello",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 30
        };

        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto> { pendingTask });

        _executorMock.ExecuteAsync(
                "echo hello",
                Arg.Any<Dictionary<string, string>>(),
                30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));

        var service = CreateService();
        await PollAndDrainAsync(service);

        await _apiClientMock.Received(1).StartTaskAsync(42, Arg.Any<CancellationToken>());

        await _apiClientMock.Received(1).CompleteTaskAsync(42, Arg.Is<TaskResultDto>(r =>
                r.Status == TaskExecutionStatus.Success && r.ExitCode == 0),
                Arg.Any<CancellationToken>());
        Assert.Equal(_options.Value.WorkDirectory,
            pendingTask.EnvironmentVariables["AETHEUS_AGENT_WORK_DIRECTORY"]);
        // Deployment scripts obtain the helper directory from the agent configuration.
        if (OperatingSystem.IsLinux())
            Assert.Equal("/usr/local/lib/aetheus", pendingTask.EnvironmentVariables["AGENT_HELPERS_DIR"]);
        else
            Assert.False(pendingTask.EnvironmentVariables.ContainsKey("AGENT_HELPERS_DIR"));
    }

    [Fact]
    public async Task PollOnceAsync_ResolvesPersistedSecretPlaceholderOnlyAtExecution()
    {
        var placeholder = PipelineSecretPlaceholder.Create("DEPLOY_TOKEN");
        var pendingTask = new PendingTaskDto
        {
            Id = 142,
            Name = "secret-task",
            Command = $"deploy --token '{placeholder}'",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["DEPLOY_TOKEN"] = "runtime-secret"
            },
            TimeoutSeconds = 30
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);
        _executorMock.ExecuteAsync(
                "deploy --token 'runtime-secret'",
                pendingTask.EnvironmentVariables,
                30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));

        await PollAndDrainAsync(CreateService());

        await _executorMock.Received(1).ExecuteAsync(
            "deploy --token 'runtime-secret'",
            pendingTask.EnvironmentVariables,
            30,
            Arg.Any<Func<string, TaskLogLevel, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_DockerBuild_HoldsMaintenanceLeaseAndFinalizesAfterFailure()
    {
        var maintenance = Substitute.For<IDockerStorageMaintenance>();
        maintenance.IsBuildCommand(Arg.Any<string>()).Returns(true);
        GrantBuildLease(maintenance);
        var pendingTask = new PendingTaskDto
        {
            Id = 43,
            Name = "container-build",
            Command = "docker buildx build --load .",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = [],
            TimeoutSeconds = 30
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);
        _executorMock.ExecuteAsync(
                pendingTask.Command, pendingTask.EnvironmentVariables, 30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(1, false));

        await PollAndDrainAsync(CreateService(dockerStorageMaintenance: maintenance));

        await maintenance.Received(1).PrepareBuildAsync(
            pendingTask.EnvironmentVariables, Arg.Any<CancellationToken>(), true, Arg.Any<TimeSpan?>());
        await maintenance.Received(1).ScheduleBuildCompletionAsync(
            true, Arg.Is<CancellationToken>(token => !token.CanBeCanceled));
        await maintenance.DidNotReceive().CompleteBuildAsync(
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task PollOnceAsync_StructuredBuildRole_HoldsSharedLeaseWithoutDockerMaintenance()
    {
        var maintenance = Substitute.For<IDockerStorageMaintenance>();
        maintenance.IsBuildCommand(Arg.Any<string>()).Returns(false);
        GrantBuildLease(maintenance);
        var pendingTask = new PendingTaskDto
        {
            Id = 44,
            PipelineRunId = 12,
            Name = "dotnet-build",
            Command = "dotnet build Aetheus.slnx -c Release",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["AETHEUS_EXECUTION_ROLE"] = "build"
            },
            TimeoutSeconds = 30
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);
        _executorMock.ExecuteAsync(
                pendingTask.Command, pendingTask.EnvironmentVariables, 30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));

        await PollAndDrainAsync(CreateService(dockerStorageMaintenance: maintenance));

        await maintenance.Received(1).PrepareBuildAsync(
            pendingTask.EnvironmentVariables, Arg.Any<CancellationToken>(), false, Arg.Any<TimeSpan?>());
        await maintenance.Received(1).ScheduleBuildCompletionAsync(
            false, Arg.Is<CancellationToken>(token => !token.CanBeCanceled));
        await maintenance.DidNotReceive().CompleteBuildAsync(
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    // The poll asks for as many tasks as it has free slots, but the local gate can already be
    // exhausted by the time the claim comes back: a finishing task releases the gate before it leaves
    // the tracking table. The old code just stopped dispatching, which stranded a task the control
    // plane had already moved to Assigned. Nothing started it, nothing handed it back, and the start
    // ceiling failed the run minutes later on a task the agent never logged as starting.
    [Fact]
    public async Task PollOnceAsync_NoFreeExecutionSlot_ReturnsTheClaimInsteadOfStrandingIt()
    {
        var options = Options.Create(new AetheusAgentOptions
        {
            ServerUrl = "http://localhost:5301",
            PollingIntervalSeconds = 1,
            MaxConcurrentTasks = 1
        });
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupying = new PendingTaskDto
        {
            Id = 71,
            Name = "occupies-the-only-slot",
            Command = "sleep",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = [],
            TimeoutSeconds = 30
        };
        var stranded = new PendingTaskDto
        {
            Id = 72,
            Name = "claimed-with-no-slot",
            Command = "echo",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = [],
            TimeoutSeconds = 30
        };
        _executorMock.ExecuteAsync(
                occupying.Command, Arg.Any<Dictionary<string, string>>(), Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await blocked.Task; return new ExecutorResult(0, false); });
        // One poll hands back both: the first takes the only slot, the second finds the gate closed.
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([occupying, stranded]);
        var service = CreateService(options: options);

        await service.PollOnceAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(1).ReleaseTaskAsync(72, Arg.Any<CancellationToken>());
        await _apiClientMock.DidNotReceive().ReleaseTaskAsync(71, Arg.Any<CancellationToken>());
        blocked.TrySetResult();
        await service.DrainRunningTasksAsync();
    }

    // Requeue a build task when the local build lease is unavailable, without executing it or
    // holding its claim until the assigned timeout.
    [Fact]
    public async Task PollOnceAsync_BuildLeaseStaysHeld_ReturnsTheTaskToTheQueueWithoutExecuting()
    {
        var maintenance = Substitute.For<IDockerStorageMaintenance>();
        maintenance.IsBuildCommand(Arg.Any<string>()).Returns(true);
        maintenance.PrepareBuildAsync(
                Arg.Any<IDictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>(),
                Arg.Any<TimeSpan?>())
            .Returns(false);
        var pendingTask = new PendingTaskDto
        {
            Id = 45,
            PipelineRunId = 13,
            Name = "second-build",
            Command = "docker buildx build --load .",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = [],
            TimeoutSeconds = 30
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);

        await PollAndDrainAsync(CreateService(dockerStorageMaintenance: maintenance));

        await _apiClientMock.Received(1).ReleaseTaskAsync(45, Arg.Any<CancellationToken>());
        await _apiClientMock.DidNotReceive().StartTaskAsync(45, Arg.Any<CancellationToken>());
        await _apiClientMock.DidNotReceive().CompleteTaskAsync(
            45, Arg.Any<TaskResultDto>(), Arg.Any<CancellationToken>());
        await _executorMock.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<int>(),
            Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>());
        await maintenance.DidNotReceive().ScheduleBuildCompletionAsync(
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // The release must beat the control plane's Assigned ceiling, otherwise the watchdog fails the task
    // before the agent ever hands it back and the fix buys nothing.
    [Fact]
    public void BuildLeaseWaitBudget_StaysUnderTheControlPlaneAssignedCeiling()
    {
        Assert.True(PollingService.BuildLeaseWaitBudget < TimeSpan.FromMinutes(2));
        Assert.True(PollingService.BuildLeaseWaitBudget > TimeSpan.Zero);
    }

    [Fact]
    public async Task PollOnceAsync_BuildLeaseWait_DoesNotStartServerExecutionTimeout()
    {
        var maintenance = Substitute.For<IDockerStorageMaintenance>();
        maintenance.IsBuildCommand(Arg.Any<string>()).Returns(false);
        var prepareEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        maintenance.PrepareBuildAsync(
                Arg.Any<IDictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                false,
                Arg.Any<TimeSpan?>())
            .Returns(async _ =>
            {
                prepareEntered.TrySetResult();
                await releasePrepare.Task;
                return true;
            });
        var pendingTask = new PendingTaskDto
        {
            Id = 441,
            PipelineRunId = 12,
            Name = "queued-build",
            Command = "dotnet test tests/Aetheus.Back.Tests",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["AETHEUS_EXECUTION_ROLE"] = "build"
            },
            TimeoutSeconds = 120
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);
        _executorMock.ExecuteAsync(
                pendingTask.Command, pendingTask.EnvironmentVariables, pendingTask.TimeoutSeconds,
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));
        var service = CreateService(dockerStorageMaintenance: maintenance);

        await service.PollOnceAsync(TestContext.Current.CancellationToken);
        await prepareEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await _apiClientMock.DidNotReceive().StartTaskAsync(441, Arg.Any<CancellationToken>());

        releasePrepare.TrySetResult();
        await service.DrainRunningTasksAsync();

        Received.InOrder(() =>
        {
            _ = maintenance.PrepareBuildAsync(
                pendingTask.EnvironmentVariables, Arg.Any<CancellationToken>(), false, Arg.Any<TimeSpan?>());
            _ = _apiClientMock.StartTaskAsync(441, Arg.Any<CancellationToken>());
            _ = _executorMock.ExecuteAsync(
                pendingTask.Command, pendingTask.EnvironmentVariables, pendingTask.TimeoutSeconds,
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task PollOnceAsync_DeploymentOnlyAgent_RefusesPipelineTaskWithoutDeployRole()
    {
        var maintenance = Substitute.For<IDockerStorageMaintenance>();
        maintenance.DeploymentOnly.Returns(true);
        var pendingTask = new PendingTaskDto
        {
            Id = 45,
            PipelineRunId = 12,
            Name = "unclassified-stage",
            Command = "echo forbidden",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = [],
            TimeoutSeconds = 30
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);

        await PollAndDrainAsync(CreateService(dockerStorageMaintenance: maintenance));

        await _apiClientMock.Received(1).StartTaskAsync(45, Arg.Any<CancellationToken>());
        await _refusalOutbox.Received(1).StoreAsync(
            Arg.Is<DeploymentBuildRefusalReport>(report =>
                report.TaskId == 45
                && report.Reason.Contains("deployment-only", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _apiClientMock.Received(1).ReportDeploymentBuildRefusalAsync(
            Arg.Is<DeploymentBuildRefusalReport>(report => report.TaskId == 45),
            Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _ = _refusalOutbox.StoreAsync(
                Arg.Is<DeploymentBuildRefusalReport>(report => report.TaskId == 45),
                Arg.Any<CancellationToken>());
            _ = _apiClientMock.StartTaskAsync(45, Arg.Any<CancellationToken>());
            _ = _apiClientMock.ReportDeploymentBuildRefusalAsync(
                Arg.Is<DeploymentBuildRefusalReport>(report => report.TaskId == 45),
                Arg.Any<CancellationToken>());
        });
        await _executorMock.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<int>(),
            Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_DeploymentRefusalNetworkFailure_RemainsInDurableOutbox()
    {
        var maintenance = Substitute.For<IDockerStorageMaintenance>();
        maintenance.DeploymentOnly.Returns(true);
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                new PendingTaskDto
                {
                    Id = 47,
                    PipelineRunId = 12,
                    Name = "build-on-deploy",
                    Command = "docker build .",
                    EnvironmentVariables = []
                }
            ]);
        _apiClientMock.ReportDeploymentBuildRefusalAsync(
                Arg.Any<DeploymentBuildRefusalReport>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("offline"));

        await PollAndDrainAsync(CreateService(dockerStorageMaintenance: maintenance));

        await _refusalOutbox.Received(1).StoreAsync(
            Arg.Is<DeploymentBuildRefusalReport>(report => report.TaskId == 47),
            Arg.Any<CancellationToken>());
        _refusalOutbox.DidNotReceive().Remove(Arg.Any<Guid>());
    }

    [Fact]
    public async Task PollOnceAsync_ReplaysPersistedDeploymentRefusalAndAcknowledgesIt()
    {
        var report = new DeploymentBuildRefusalReport
        {
            IncidentId = Guid.NewGuid(),
            TaskId = 48,
            OccurredAtUtc = DateTime.UtcNow,
            Reason = "deployment-only refusal"
        };
        _refusalOutbox.ReadAllAsync(Arg.Any<CancellationToken>()).Returns([report]);
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await CreateService().PollOnceAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(1).ReportDeploymentBuildRefusalAsync(
            report,
            Arg.Any<CancellationToken>());
        _refusalOutbox.Received(1).Remove(report.IncidentId);
    }

    [Fact]
    public async Task PollOnceAsync_DeploymentOnlyAgent_AllowsExplicitDeployRole()
    {
        var maintenance = Substitute.For<IDockerStorageMaintenance>();
        maintenance.DeploymentOnly.Returns(true);
        var pendingTask = new PendingTaskDto
        {
            Id = 46,
            PipelineRunId = 12,
            Name = "deploy-stage",
            Command = "docker compose up -d",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["AETHEUS_EXECUTION_ROLE"] = "deploy"
            },
            TimeoutSeconds = 30
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);
        _executorMock.ExecuteAsync(
                pendingTask.Command, pendingTask.EnvironmentVariables, 30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));

        await PollAndDrainAsync(CreateService(dockerStorageMaintenance: maintenance));

        await _executorMock.Received(1).ExecuteAsync(
            pendingTask.Command, pendingTask.EnvironmentVariables, 30,
            Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>());
        await _apiClientMock.Received(1).CompleteTaskAsync(46,
            Arg.Is<TaskResultDto>(result => result.Status == TaskExecutionStatus.Success),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_ReportsFailedTask()
    {
        var pendingTask = new PendingTaskDto
        {
            Id = 99,
            Name = "fail-task",
            Command = "exit 1",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 30
        };

        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto> { pendingTask });

        _executorMock.ExecuteAsync(
                "exit 1",
                Arg.Any<Dictionary<string, string>>(),
                30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(1, false));

        var service = CreateService();
        await PollAndDrainAsync(service);

        await _apiClientMock.Received(1).CompleteTaskAsync(99, Arg.Is<TaskResultDto>(r =>
                r.Status == TaskExecutionStatus.Failed && r.ExitCode == 1),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_SuccessfulSelfUpdate_CompletesBackendTaskBeforeRestartingProcess()
    {
        var operation = Substitute.For<IOperationExecutor>();
        operation.CanHandle(OperationKind.AgentSelfUpdate).Returns(true);
        operation.ExecuteAsync(
                OperationKind.AgentSelfUpdate,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));

        var completionObserved = false;
        _apiClientMock.CompleteTaskAsync(120, Arg.Any<TaskResultDto>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                completionObserved = true;
                return Task.CompletedTask;
            });
        _processRestarter.When(x => x.Restart(Arg.Any<string>()))
            .Do(_ => Assert.True(completionObserved));

        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                new PendingTaskDto
                {
                    Id = 120,
                    Name = "self-update",
                    Operation = OperationKind.AgentSelfUpdate,
                    EnvironmentVariables = [],
                    TimeoutSeconds = 60
                }
            ]);

        await PollAndDrainAsync(CreateService([operation]));

        await _apiClientMock.Received(1).CompleteTaskAsync(120,
            Arg.Is<TaskResultDto>(result => result.Status == TaskExecutionStatus.Success),
            CancellationToken.None);
        _processRestarter.Received(1).Restart(Arg.Any<string>());
    }

    [Fact]
    public async Task PollOnceAsync_FailedSelfUpdate_DoesNotRestartProcess()
    {
        var operation = Substitute.For<IOperationExecutor>();
        operation.CanHandle(OperationKind.AgentSelfUpdate).Returns(true);
        operation.ExecuteAsync(
                OperationKind.AgentSelfUpdate,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(1, false));
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                new PendingTaskDto
                {
                    Id = 121,
                    Name = "self-update",
                    Operation = OperationKind.AgentSelfUpdate,
                    EnvironmentVariables = [],
                    TimeoutSeconds = 60
                }
            ]);

        await PollAndDrainAsync(CreateService([operation]));

        _processRestarter.DidNotReceive().Restart(Arg.Any<string>());
    }

    [Fact]
    public async Task PollOnceAsync_SelfUpdateWithTrackedProcess_RemainsPendingUntilAgentIsIdle()
    {
        var operation = Substitute.For<IOperationExecutor>();
        operation.CanHandle(OperationKind.AgentSelfUpdate).Returns(true);
        var health = new AgentRuntimeHealth(TimeProvider.System);
        using var process = Process.GetCurrentProcess();
        using var activity = health.TrackProcess(process);
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                new PendingTaskDto
                {
                    Id = 123,
                    Name = "self-update",
                    Operation = OperationKind.AgentSelfUpdate,
                    EnvironmentVariables = [],
                    TimeoutSeconds = 60
                }
            ]);
        var service = CreateService([operation], runtimeHealth: health);

        await service.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(service._runningTasks);
        await operation.DidNotReceive().ExecuteAsync(
            Arg.Any<OperationKind>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<int>(),
            Arg.Any<Func<string, TaskLogLevel, Task>>(),
            Arg.Any<CancellationToken>());
        _processRestarter.DidNotReceive().Restart(Arg.Any<string>());
    }

    [Fact]
    public async Task PollOnceAsync_SuccessfulSelfUpdate_RestartsEvenWhenFinalAcknowledgementFails()
    {
        var operation = Substitute.For<IOperationExecutor>();
        operation.CanHandle(OperationKind.AgentSelfUpdate).Returns(true);
        operation.ExecuteAsync(
                OperationKind.AgentSelfUpdate,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));
        _apiClientMock.CompleteTaskAsync(122, Arg.Any<TaskResultDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("control plane unavailable"));
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                new PendingTaskDto
                {
                    Id = 122,
                    Name = "self-update",
                    Operation = OperationKind.AgentSelfUpdate,
                    EnvironmentVariables = [],
                    TimeoutSeconds = 60
                }
            ]);

        await PollAndDrainAsync(CreateService([operation]));

        _processRestarter.Received(1).Restart(Arg.Any<string>());
    }

    [Fact]
    public async Task PollOnceAsync_ApiFailure_DoesNotCorruptNextPoll()
    {
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new HttpRequestException("Server unreachable"),
                _ => new List<PendingTaskDto>());

        var service = CreateService();

        // First poll throws (the loop swallows it and retries next tick); the SECOND poll
        // must still reach the server. Two real iterations replace "sleep 4s, hope ≥2 calls".
        await Assert.ThrowsAsync<HttpRequestException>(() => service.PollOnceAsync(TestContext.Current.CancellationToken));
        await service.PollOnceAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(2).GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_TaskReportFailure_DoesNotCorruptNextPoll()
    {
        var pendingTask = new PendingTaskDto
        {
            Id = 50,
            Name = "report-fail-task",
            Command = "echo ok",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 30
        };

        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto> { pendingTask }, new List<PendingTaskDto>());

        _executorMock.ExecuteAsync(
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));

        _apiClientMock.CompleteTaskAsync(50, Arg.Any<TaskResultDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Failed to report"));

        var service = CreateService();

        // The completion report throws inside the dispatched task; the failure is contained
        // (does not bubble out of the poll loop) and the next poll still runs.
        await PollAndDrainAsync(service);
        await service.PollOnceAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received().CompleteTaskAsync(50, Arg.Any<TaskResultDto>(), Arg.Any<CancellationToken>());
        await _apiClientMock.Received(2).GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NotEnrolled_DoesNotPoll()
    {
        var notEnrolledState = new AgentState(TimeProvider.System);
        var notEnrolled = new EnrollmentService(
            _apiClientMock,
            _options,
            notEnrolledState,
            Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>(),
            Substitute.For<ICredentialProtector>(),
            Substitute.For<IShellRunner>(),
            TimeProvider.System,
            NullLogger<EnrollmentService>.Instance);

        var service = new PollingService(
            _apiClientMock,
            notEnrolled,
            [_executorMock],
            Array.Empty<IOperationExecutor>(),
            Substitute.For<IContainerExecutor>(),
            ContainerFunctions(Substitute.For<IContainerExecutor>()),
            Substitute.For<IDockerStorageMaintenance>(),
            _refusalOutbox,
            notEnrolledState,
            new AgentRuntimeHealth(TimeProvider.System),
            _processRestarter,
            TimeProvider.System,
            _options,
            NullLogger<PollingService>.Instance);

        // While not enrolled the loop parks on its wait-gate and never reaches a poll.
        // Start then immediately stop: the gate is never crossed - deterministic, no sleep.
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await cts.CancelAsync();
        await service.StopAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.DidNotReceive().GetPendingTasksAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private EnrollmentService CreateEnrolledEnrollmentService()
    {
        var mockConfig = Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>();
        return new EnrollmentService(
            _apiClientMock,
            _options,
            _agentState,
            mockConfig,
            Substitute.For<ICredentialProtector>(),
            Substitute.For<IShellRunner>(),
            TimeProvider.System,
            NullLogger<EnrollmentService>.Instance);
    }

    [Fact]
    public async Task PollOnceAsync_NoExecutorForType_ReportsFailedImmediately()
    {
        var pendingTask = new PendingTaskDto
        {
            Id = 77,
            Name = "docker-task",
            Command = "run container",
            Executor = ExecutorType.Docker,
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 30
        };

        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto> { pendingTask });

        var service = CreateService();
        await PollAndDrainAsync(service);

        await _apiClientMock.Received(1).CompleteTaskAsync(77,
            Arg.Is<TaskResultDto>(r => r.Status == TaskExecutionStatus.Failed && r.ExitCode == -1),
            Arg.Any<CancellationToken>());

        await _apiClientMock.Received(1).StartTaskAsync(77, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_TimedOutTask_ReportsTimeout()
    {
        var pendingTask = new PendingTaskDto
        {
            Id = 88,
            Name = "slow-task",
            Command = "sleep 999",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 1
        };

        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto> { pendingTask });

        _executorMock.ExecuteAsync(
                "sleep 999", Arg.Any<Dictionary<string, string>>(), 1,
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(-1, true));

        var service = CreateService();
        await PollAndDrainAsync(service);

        await _apiClientMock.Received(1).CompleteTaskAsync(88,
            Arg.Is<TaskResultDto>(r => r.Status == TaskExecutionStatus.Timeout),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_OperationTask_UsesOperationExecutor()
    {
        var operationExecutorMock = Substitute.For<IOperationExecutor>();
        operationExecutorMock.CanHandle(OperationKind.DockerRestartContainer).Returns(true);
        // Item #5.3: setup the env-var-aware overload that PollingService now calls.
        operationExecutorMock.ExecuteAsync(
                OperationKind.DockerRestartContainer, "nginx",
                Arg.Any<IReadOnlyDictionary<string, string>>(), 30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));

        var pendingTask = new PendingTaskDto
        {
            Id = 100,
            Name = "restart-container",
            Command = "nginx",
            Operation = OperationKind.DockerRestartContainer,
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 30
        };

        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto> { pendingTask });

        var service = CreateService([operationExecutorMock]);
        await PollAndDrainAsync(service);

        await _apiClientMock.Received(1).StartTaskAsync(100, Arg.Any<CancellationToken>());
        // Item #5.3: PollingService now passes env vars to the typed-op executor (new overload).
        await operationExecutorMock.Received(1).ExecuteAsync(
            OperationKind.DockerRestartContainer, "nginx",
            Arg.Any<IReadOnlyDictionary<string, string>>(), 30,
            Arg.Any<Func<string, TaskLogLevel, Task>>(),
            Arg.Any<CancellationToken>());
        await _apiClientMock.Received(1).CompleteTaskAsync(100,
            Arg.Is<TaskResultDto>(r => r.Status == TaskExecutionStatus.Success),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_ContainerizedTypedOperation_CannotBypassContainerIsolation()
    {
        var operationExecutor = Substitute.For<IOperationExecutor>();
        operationExecutor.CanHandle(OperationKind.AiRun).Returns(true);
        var containerExecutor = Substitute.For<IContainerExecutor>();
        containerExecutor.ExecuteAsync(
                Arg.Any<ContainerSpec>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));
        var pendingTask = new PendingTaskDto
        {
            Id = 102,
            Name = "isolated-ai",
            Command = "ai-run",
            Operation = OperationKind.AiRun,
            Container = new ContainerSpec { Image = "runner@sha256:abc" },
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 30
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);
        var service = CreateService([operationExecutor], containerExecutor: containerExecutor);

        await PollAndDrainAsync(service);

        await containerExecutor.Received(1).ExecuteAsync(
            pendingTask.Container,
            "ai-run",
            Arg.Any<Dictionary<string, string>>(),
            30,
            Arg.Any<Func<string, TaskLogLevel, Task>>(),
            Arg.Any<CancellationToken>());
        await operationExecutor.DidNotReceive().ExecuteAsync(
            OperationKind.AiRun,
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<int>(),
            Arg.Any<Func<string, TaskLogLevel, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_ContainerizedFunction_RunsAetheusFnInsideTheStepContainer()
    {
        Directory.CreateDirectory(_functionsDirectory);
        try
        {
            foreach (var executable in ContainerPipelineFunctionExecutor.Executables)
            {
                var path = Path.Combine(_functionsDirectory, executable);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "binary", TestContext.Current.CancellationToken);
            }
            var hostFunction = Substitute.For<IOperationExecutor>();
            hostFunction.CanHandle(OperationKind.PipelineFunction).Returns(true);
            var containerExecutor = Substitute.For<IContainerExecutor>();
            containerExecutor.ExecuteAsync(
                    Arg.Any<ContainerSpec>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(),
                    Arg.Any<int>(), Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>(),
                    Arg.Any<IReadOnlyList<ContainerReadOnlyMount>?>())
                .Returns(new ExecutorResult(0, false));
            var pendingTask = new PendingTaskDto
            {
                Id = 103,
                Name = "Detect languages",
                Command = "detect-project-languages",
                Operation = OperationKind.PipelineFunction,
                Container = new ContainerSpec { Image = "alpine@sha256:abc", Shell = "sh" },
                EnvironmentVariables = new Dictionary<string, string> { ["WORKSPACE"] = "/w" },
                TimeoutSeconds = 30
            };
            _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([pendingTask]);
            var service = CreateService([hostFunction], containerExecutor: containerExecutor);

            await PollAndDrainAsync(service);

            await containerExecutor.Received(1).ExecuteAsync(
                pendingTask.Container,
                Arg.Is<string>(command => command.Contains("exec \"$fn\" 'detect-project-languages'", StringComparison.Ordinal)),
                Arg.Is<Dictionary<string, string>>(env => env["WORKSPACE"] == "/w"),
                30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>(),
                Arg.Is<IReadOnlyList<ContainerReadOnlyMount>?>(mounts =>
                    mounts != null && mounts.Single() == new ContainerReadOnlyMount(_functionsDirectory, "/opt/aetheus-fn")));
            await hostFunction.DidNotReceiveWithAnyArgs().ExecuteAsync(
                default, default!, default!, default, default!, TestContext.Current.CancellationToken);
            await _apiClientMock.Received(1).CompleteTaskAsync(103,
                Arg.Is<TaskResultDto>(r => r.Status == TaskExecutionStatus.Success),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(_functionsDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, false, "InfrastructureMismatch", "Toolchain manifest is invalid.", TaskExecutionStatus.Failed)]
    [InlineData(-1, true, "ToolError", "Container timed out.", TaskExecutionStatus.Timeout)]
    [InlineData(1, false, "BuildFailed", "Compilation failed.", TaskExecutionStatus.Failed)]
    [InlineData(1, false, "TestsFailed", "Two tests failed.", TaskExecutionStatus.Failed)]
    public async Task PollOnceAsync_TransportsStructuredFailureExactly(
        int exitCode,
        bool timedOut,
        string failureCode,
        string failureReason,
        TaskExecutionStatus expectedStatus)
    {
        var pendingTask = new PendingTaskDto
        {
            Id = 991,
            Name = "structured-failure",
            Command = "execute",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = [],
            TimeoutSeconds = 30
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);
        _executorMock.ExecuteAsync(
                pendingTask.Command,
                Arg.Any<Dictionary<string, string>>(),
                pendingTask.TimeoutSeconds,
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(exitCode, timedOut, failureCode, failureReason));

        await PollAndDrainAsync(CreateService());

        await _apiClientMock.Received(1).CompleteTaskAsync(
            pendingTask.Id,
            Arg.Is<TaskResultDto>(result =>
                result.Status == expectedStatus
                && result.ExitCode == exitCode
                && result.FailureCode == failureCode
                && result.FailureReason == failureReason),
            CancellationToken.None);
    }

    [Fact]
    public async Task PollOnceAsync_PurgeWorkspace_RemovesBothTreesEvenAfterExecutionFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aetheus-purge-{Guid.NewGuid():N}");
        var pipelineRunId = 741;
        var workspace = Path.Combine(root, "cw", pipelineRunId.ToString());
        var state = Path.Combine(root, "container-state", pipelineRunId.ToString());
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "source.txt"),
            "source",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(state, "state.json"),
            "{}",
            TestContext.Current.CancellationToken);
        var pendingTask = new PendingTaskDto
        {
            Id = 992,
            PipelineRunId = pipelineRunId,
            PurgeWorkspace = true,
            Name = "cleanup-after-failure",
            Command = "fail",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = [],
            TimeoutSeconds = 30
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);
        _executorMock.ExecuteAsync(
                pendingTask.Command,
                Arg.Any<Dictionary<string, string>>(),
                pendingTask.TimeoutSeconds,
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated execution failure"));
        var options = Options.Create(new AetheusAgentOptions
        {
            ServerUrl = "http://localhost:5301",
            WorkDirectory = root,
            PollingIntervalSeconds = 1,
            MaxConcurrentTasks = 2
        });

        try
        {
            await PollAndDrainAsync(CreateService(options: options));

            Assert.False(Directory.Exists(workspace));
            Assert.False(Directory.Exists(state));
            await _apiClientMock.Received(1).CompleteTaskAsync(
                pendingTask.Id,
                Arg.Is<TaskResultDto>(result => result.Status == TaskExecutionStatus.Failed),
                CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PollOnceAsync_ContainerArtifactOperation_UsesDaemonVisibleWorkspace()
    {
        var operation = Substitute.For<IOperationExecutor>();
        operation.CanHandle(OperationKind.PipelineCollectArtifacts).Returns(true);
        operation.ExecuteAsync(
                OperationKind.PipelineCollectArtifacts,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(0, false));
        var pendingTask = new PendingTaskDto
        {
            Id = 103,
            PipelineRunId = 42,
            Name = "Collect Artifacts",
            Command = "[\"out/**\"]",
            Operation = OperationKind.PipelineCollectArtifacts,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["AETHEUS_WORKSPACE_MODE"] = "container",
                ["AETHEUS_WORKING_DIR"] = "/w"
            },
            TimeoutSeconds = 30
        };
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pendingTask]);

        await PollAndDrainAsync(CreateService([operation]));

        await operation.Received(1).ExecuteAsync(
            OperationKind.PipelineCollectArtifacts,
            pendingTask.Command,
            Arg.Is<IReadOnlyDictionary<string, string>>(env =>
                env["AETHEUS_WORKING_DIR"]
                    == Path.Combine(_options.Value.WorkDirectory, "cw", "42")),
            30,
            Arg.Any<Func<string, TaskLogLevel, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_OperationTask_NoHandler_ReportsFailure()
    {
        var pendingTask = new PendingTaskDto
        {
            Id = 101,
            Name = "unknown-operation",
            Command = "target",
            Operation = OperationKind.DockerRestartContainer,
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 30
        };

        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto> { pendingTask });

        var service = CreateService();
        await PollAndDrainAsync(service);

        await _apiClientMock.Received(1).CompleteTaskAsync(101,
            Arg.Is<TaskResultDto>(r => r.Status == TaskExecutionStatus.Failed && r.ExitCode == -1),
            Arg.Any<CancellationToken>());
        await _apiClientMock.Received(1).StartTaskAsync(101, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_OperationTask_Failed_ReportsFailedStatus()
    {
        var operationExecutorMock = Substitute.For<IOperationExecutor>();
        operationExecutorMock.CanHandle(OperationKind.ServiceRestart).Returns(true);
        operationExecutorMock.ExecuteAsync(
                OperationKind.ServiceRestart, "nginx",
                Arg.Any<IReadOnlyDictionary<string, string>>(), 30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(1, false));

        var pendingTask = new PendingTaskDto
        {
            Id = 102,
            Name = "restart-service",
            Command = "nginx",
            Operation = OperationKind.ServiceRestart,
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 30
        };

        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto> { pendingTask });

        var health = new AgentRuntimeHealth(TimeProvider.System);
        var service = CreateService([operationExecutorMock], runtimeHealth: health);
        await PollAndDrainAsync(service);

        await _apiClientMock.Received(1).CompleteTaskAsync(102,
            Arg.Is<TaskResultDto>(r => r.Status == TaskExecutionStatus.Failed && r.ExitCode == 1),
            Arg.Any<CancellationToken>());
        // A finished service action asks for a heartbeat, so the page sees the new state.
        Assert.True(health.WaitForHeartbeatRequestAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        // A service action does not touch the cached mail inventory.
        Assert.False(health.ConsumeMailInventoryInvalidation());
    }

    [Theory]
    [InlineData(OperationKind.MailInstallCertificate, 0)]
    [InlineData(OperationKind.MailServiceControl, 0)]
    [InlineData(OperationKind.MailAddDomain, 1)]
    [InlineData(OperationKind.MailAddAccount, 0)]
    public async Task R1_8_ACompletedMailOperation_InvalidatesTheMailInventory_AndAsksForAHeartbeat(OperationKind operation, int exitCode)
    {
        var operationExecutorMock = Substitute.For<IOperationExecutor>();
        operationExecutorMock.CanHandle(operation).Returns(true);
        operationExecutorMock.ExecuteAsync(
                operation, "mail.example.com",
                Arg.Any<IReadOnlyDictionary<string, string>>(), 30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutorResult(exitCode, false));
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto>
            {
                new()
                {
                    Id = 103,
                    Name = "mail",
                    Command = "mail.example.com",
                    Operation = operation,
                    Executor = ExecutorType.Shell,
                    EnvironmentVariables = new Dictionary<string, string>(),
                    TimeoutSeconds = 30
                }
            });

        var health = new AgentRuntimeHealth(TimeProvider.System);
        await PollAndDrainAsync(CreateService([operationExecutorMock], runtimeHealth: health));

        await _apiClientMock.Received(1).CompleteTaskAsync(103, Arg.Any<TaskResultDto>(), Arg.Any<CancellationToken>());
        Assert.True(health.ConsumeMailInventoryInvalidation());
        Assert.True(health.WaitForHeartbeatRequestAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(OperationKind.MailViewQueue, false)]
    [InlineData(OperationKind.MailGetLogs, false)]
    [InlineData(OperationKind.MailTestConfig, false)]
    [InlineData(OperationKind.MailQuotaReport, false)]
    [InlineData(OperationKind.MailOutboundProbe, false)]
    [InlineData(OperationKind.ServiceRestart, false)]
    [InlineData(OperationKind.MailInstallCertificate, true)]
    [InlineData(OperationKind.MailDkimRotate, true)]
    [InlineData(OperationKind.MailRestartPostfix, true)]
    [InlineData(OperationKind.MailSendTest, true)]
    [InlineData(OperationKind.MailPurge, true)]
    public void R1_8_OnlyTheMailOperationsThatChangeTheStack_InvalidateItsInventory(OperationKind operation, bool invalidates) =>
        Assert.Equal(invalidates, PollingService.ChangesMailInventory(operation));

    [Fact]
    public async Task PollOnceAsync_ExecutorThrows_ReportsFailedAndContinues()
    {
        var pendingTask = new PendingTaskDto
        {
            Id = 66,
            Name = "crash-task",
            Command = "crash",
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 30
        };

        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto> { pendingTask });

        static async Task<ExecutorResult> CrashAfterOutputAsync(
            Func<string, TaskLogLevel, Task> onOutput)
        {
            await onOutput("executor started", TaskLogLevel.Info);
            throw new InvalidOperationException("Executor crashed");
        }

        _executorMock.ExecuteAsync(
                "crash", Arg.Any<Dictionary<string, string>>(), 30,
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => CrashAfterOutputAsync(
                call.ArgAt<Func<string, TaskLogLevel, Task>>(3)));

        var service = CreateService();
        await PollAndDrainAsync(service);

        await _apiClientMock.Received(1).CompleteTaskAsync(66,
            Arg.Is<TaskResultDto>(r =>
                r.Status == TaskExecutionStatus.Failed
                && r.ExitCode == -1
                && r.FailureCode == TaskFailureCodes.ToolError
                && r.FailureReason != null
                && r.FailureReason.Contains("InvalidOperationException", StringComparison.Ordinal)
                && r.FailureReason.Contains("Executor crashed", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _apiClientMock.Received().AppendLogBatchAsync(
            Arg.Is<List<AppendLogRequest>>(batch =>
                batch.Any(log => log.Message == "executor started")),
            Arg.Any<CancellationToken>());
        await _apiClientMock.Received().AppendLogBatchAsync(
            Arg.Is<List<AppendLogRequest>>(batch =>
                batch.Any(log => log.Level == TaskLogLevel.Error
                                 && log.Message.Contains("Executor crashed", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_EnforcesMaximumConcurrency_AndReleasesGateAfterFailure()
    {
        var pending = Enumerable.Range(1, 3).Select(id => new PendingTaskDto
        {
            Id = id,
            Name = $"blocked-{id}",
            Command = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Executor = ExecutorType.Shell,
            EnvironmentVariables = new Dictionary<string, string>(),
            TimeoutSeconds = 30
        }).ToArray();
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([.. pending], [pending[2]]);
        var releases = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var started = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var current = 0;
        var maximum = 0;
        _executorMock.ExecuteAsync(
                Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => ExecuteBlockedAsync(call.ArgAt<string>(0)));
        var service = CreateService();

        await service.PollOnceAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(started[1].Task, started[2].Task)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.False(started[3].Task.IsCompleted);
        Assert.Equal(2, Volatile.Read(ref maximum));
        Assert.Equal(2, service._runningTasks.Count);

        var failedExecution = service._runningTasks[1].Execution;
        releases[1].TrySetResult();
        await failedExecution.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(service._runningTasks.ContainsKey(1));
        await service.PollOnceAsync(TestContext.Current.CancellationToken);
        await started[3].Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref maximum));
        releases[2].TrySetResult();
        releases[3].TrySetResult();
        await service.DrainRunningTasksAsync();
        Assert.Equal(0, Volatile.Read(ref current));

        async Task<ExecutorResult> ExecuteBlockedAsync(string command)
        {
            var id = int.Parse(command, System.Globalization.CultureInfo.InvariantCulture);
            var active = Interlocked.Increment(ref current);
            UpdateMaximum(ref maximum, active);
            started[id].TrySetResult();
            try
            {
                await releases[id].Task;
                if (id == 1)
                    throw new InvalidOperationException("intentional executor failure");
                return new ExecutorResult(0, false);
            }
            finally
            {
                Interlocked.Decrement(ref current);
            }
        }
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (candidate <= observed) return;
        } while (Interlocked.CompareExchange(ref maximum, candidate, observed) != observed);
    }

    // Reconciliation: a task reported terminal server-side is cancelled locally - this is the trigger
    // that unwinds ExecuteTaskAsync, whose finally releases the concurrency gate. We assert the
    // cancellation (the reconcile responsibility); the unconditional gate release is the C# finally.
    [Fact]
    public async Task ReconcileRunningTasksAsync_TerminalStatus_CancelsTrackedTask()
    {
        var service = CreateService();
        var cts = new CancellationTokenSource();
        service._runningTasks[42] = new PollingService.TrackedTask(Task.CompletedTask, cts);
        _apiClientMock.GetTaskStatusesAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [42] = nameof(TaskExecutionStatus.Cancelled) });

        await service.ReconcileRunningTasksAsync(TestContext.Current.CancellationToken);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task ReconcileRunningTasksAsync_NonTerminalStatus_DoesNotCancel()
    {
        var service = CreateService();
        var cts = new CancellationTokenSource();
        service._runningTasks[42] = new PollingService.TrackedTask(Task.CompletedTask, cts);
        _apiClientMock.GetTaskStatusesAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [42] = nameof(TaskExecutionStatus.Running) });

        await service.ReconcileRunningTasksAsync(TestContext.Current.CancellationToken);

        Assert.False(cts.IsCancellationRequested);
    }

    // While a self-update is tracked the agent is about to exit, so it claims nothing.
    [Fact]
    public async Task PollOnceAsync_WhileASelfUpdateIsTracked_AsksForNoSlot()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        service._runningTasks[42] = new PollingService.TrackedTask(new TaskCompletionSource().Task, cts, IsSelfUpdate: true);
        _apiClientMock.GetTaskStatusesAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [42] = nameof(TaskExecutionStatus.Running) });
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto>());

        await service.PollOnceAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(1).GetPendingTasksAsync(1, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_WhileAnOrdinaryTaskIsTracked_StillAsksForTheFreeSlots()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        service._runningTasks[42] = new PollingService.TrackedTask(new TaskCompletionSource().Task, cts);
        _apiClientMock.GetTaskStatusesAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [42] = nameof(TaskExecutionStatus.Running) });
        _apiClientMock.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingTaskDto>());

        await service.PollOnceAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(1).GetPendingTasksAsync(1, _options.Value.MaxConcurrentTasks - 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BoundFailureReason_CapsAReasonLongerThanTheServerAcceptsAt512Characters()
    {
        var reason = new string('x', 1_500);

        Assert.Equal(512, PollingService.BoundFailureReason(reason)!.Length);
        Assert.Equal("short", PollingService.BoundFailureReason("short"));
        Assert.Null(PollingService.BoundFailureReason(null));
    }
}
