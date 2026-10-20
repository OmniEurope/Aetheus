// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class TaskServiceTests
{
    private readonly ITaskRepository _repo = Substitute.For<ITaskRepository>();
    // Tasks announces settled steps through the dispatcher, and Pipelines handles their
    // consequences.
    private readonly Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher _domainEvents =
        Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>();
    private readonly ILogService _logService = Substitute.For<ILogService>();
    private readonly IHubContext<PipelineHub> _pipelineHub = Substitute.For<IHubContext<PipelineHub>>();
    private readonly IHubContext<ServerHub> _serverHub = Substitute.For<IHubContext<ServerHub>>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly Aetheus.Back.Services.IEncryptionService _encryption = Substitute.For<Aetheus.Back.Services.IEncryptionService>();
    private readonly IArtifactService _artifactService = Substitute.For<IArtifactService>();
    private readonly TaskService _sut;

    public TaskServiceTests()
    {
        var pipelineClients = Substitute.For<IHubClients>();
        var pipelineProxy = Substitute.For<IClientProxy>();
        pipelineClients.Group(Arg.Any<string>()).Returns(pipelineProxy);
        _pipelineHub.Clients.Returns(pipelineClients);

        var serverClients = Substitute.For<IHubClients>();
        var serverProxy = Substitute.For<IClientProxy>();
        serverClients.Group(Arg.Any<string>()).Returns(serverProxy);
        _serverHub.Clients.Returns(serverClients);

        // Encryption pass-through: env-protection behavior is covered by TaskEnvProtection tests;
        // here we keep stored == plaintext so existing assertions on EnvironmentVariables hold.
        _encryption.EncryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _encryption.DecryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _repo.TryStartTaskAsync(
                Arg.Any<ServerTask>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var task = call.ArgAt<ServerTask>(0);
                task.Status = TaskExecutionStatus.Running;
                task.StartedAt = call.ArgAt<DateTime>(1);
                return true;
            });
        var taskQueueNotifier = new TaskQueueNotifier(
            _serverHub,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<TaskQueueNotifier>>());
        _sut = new TaskService(_repo, _logService, _pipelineHub, _serverHub, _auditMock, TimeProvider.System, _encryption, _artifactService, taskQueueNotifier,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<TaskService>>(), _domainEvents);
    }

    // --- GetTasksAsync ---

    [Fact]
    public async Task GetTasksAsync_ReturnsMappedPaginatedResult()
    {
        var tasks = new List<ServerTask>
        {
            new() { Id = 1, ServerId = 1, Name = "deploy", Command = "ls", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Success, Server = new Server { Name = "srv1" } }
        };
        _repo.GetTasksPagedAsync(null, 1, 10, null, null, null, Arg.Any<CancellationToken>(), null, false)
            .Returns((tasks, 1));

        var result = await _sut.GetTasksAsync(new TaskPaginationRequest { Page = 1, PageSize = 10 }, accessibleServerIds: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("deploy", result.Items[0].Name);
        Assert.Equal("srv1", result.Items[0].ServerName);
    }

    [Fact]
    public async Task GetTasksAsync_WithStatusFilter_PassesStatusToRepository()
    {
        _repo.GetTasksPagedAsync(null, 1, 25, TaskExecutionStatus.Success, null, null, Arg.Any<CancellationToken>(), null, false)
            .Returns((new List<ServerTask>(), 0));

        await _sut.GetTasksAsync(new TaskPaginationRequest { Status = TaskExecutionStatus.Success }, accessibleServerIds: null, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).GetTasksPagedAsync(null, 1, 25, TaskExecutionStatus.Success, null, null, Arg.Any<CancellationToken>(), null, false);
    }

    [Fact]
    public async Task GetTasksAsync_WithServerId_PassesServerIdToRepository()
    {
        _repo.GetTasksPagedAsync(null, 1, 25, null, null, 15, Arg.Any<CancellationToken>(), null, false)
            .Returns((new List<ServerTask>(), 0));

        await _sut.GetTasksAsync(new TaskPaginationRequest { ServerId = 15 }, accessibleServerIds: null, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).GetTasksPagedAsync(null, 1, 25, null, null, 15, Arg.Any<CancellationToken>(), null, false);
    }

    // --- GetTaskAsync ---

    [Fact]
    public async Task GetTaskAsync_Found_ReturnsDto()
    {
        _repo.GetTaskWithServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ServerTask { Id = 1, ServerId = 1, Name = "test", Command = "echo", Server = new Server { Name = "s" } });

        var result = await _sut.GetTaskAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("test", result.Name);
    }

    [Fact]
    public async Task GetTaskAsync_NotFound_ReturnsNull()
    {
        _repo.GetTaskWithServerAsync(99, Arg.Any<CancellationToken>())
            .Returns((ServerTask?)null);

        var result = await _sut.GetTaskAsync(99, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    // --- CreateTaskAsync ---

    [Fact]
    public async Task CreateTaskAsync_CreatesAndReturnsDto()
    {
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var t = ci.Arg<ServerTask>();
                t.Id = 42;
                return t;
            });

        var result = await _sut.CreateTaskAsync(new CreateTaskRequest
        {
            ServerId = 1,
            Name = "build",
            Command = "dotnet build",
            Executor = ExecutorType.Shell,
            TimeoutSeconds = 60
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("build", result.Name);
        Assert.Equal(42, result.Id);
        await _auditMock.Received(1).LogAsync("Created", "Task", 42, "build", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTaskAsync_OfflineServer_ThrowsConflict_AndCreatesNothing()
    {
        _repo.GetServerStatusAsync(1, Arg.Any<CancellationToken>()).Returns(ServerStatus.Offline);

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.ConflictException>(() =>
            _sut.CreateTaskAsync(new CreateTaskRequest { ServerId = 1, Name = "build", Command = "dotnet build" }, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    // --- GetPendingTasksAsync ---

    [Fact]
    public async Task GetPendingTasksAsync_MarksAsAssigned()
    {
        var tasks = new List<ServerTask>
        {
            new() { Id = 1, ServerId = 1, Name = "t1", Command = "c", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Pending },
            new() { Id = 2, ServerId = 1, Name = "t2", Command = "c", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Pending }
        };
        _repo.ClaimPendingTasksAsync(
            1,
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>()).Returns(tasks);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.GetPendingTasksAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        await _repo.Received(1).ClaimPendingTasksAsync(
            1,
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPendingTasksAsync_SystemCleanupCarriesWorkspacePurgeContract()
    {
        var cleanupStep = new PipelineStepRun
        {
            IsSystem = true,
            StageName = PipelineRunService.SystemCleanupStage,
            StepName = "Cleanup"
        };
        _repo.ClaimPendingTasksAsync(
                1,
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns([
                new ServerTask
                {
                    Id = 17,
                    ServerId = 1,
                    PipelineRunId = 91,
                    Name = "Cleanup",
                    Command = "cleanup",
                    PipelineStepRun = cleanupStep
                }
            ]);

        var result = await _sut.GetPendingTasksAsync(
            1,
            agentSessionId: "session",
            ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.True(result[0].PurgeWorkspace);
    }

    // --- StartTaskAsync ---

    [Fact]
    public async Task StartTaskAsync_PendingTask_SetsRunning()
    {
        var task = new ServerTask { Id = 1, Status = TaskExecutionStatus.Pending };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.StartTaskAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
        Assert.NotNull(task.StartedAt);
    }

    [Fact]
    public async Task StartTaskAsync_AssignedTask_SetsRunning()
    {
        var task = new ServerTask { Id = 1, Status = TaskExecutionStatus.Assigned };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.StartTaskAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
    }

    [Fact]
    public async Task StartTaskAsync_StaleFencingToken_DoesNotTransition()
    {
        var task = new ServerTask { Id = 1, ServerId = 1, Status = TaskExecutionStatus.Assigned };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.TryStartTaskWithLeaseAsync(
                task,
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.StartTaskAsync(
            1,
            new AgentTaskLeaseRequest
            {
                AgentSessionId = "11111111111111111111111111111111",
                AgentSessionFencingToken = 1
            },
            TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(TaskExecutionStatus.Assigned, task.Status);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static readonly AgentTaskLeaseRequest ReleaseLease = new()
    {
        AgentSessionId = "11111111111111111111111111111111",
        AgentSessionFencingToken = 3
    };

    [Fact]
    public async Task ReleaseAssignedTaskAsync_AnAssignedTaskNeverStarted_IsHandedBackAndSaved()
    {
        var task = new ServerTask { Id = 7, ServerId = 2, Status = TaskExecutionStatus.Assigned };
        _repo.FindTaskAsync(7, Arg.Any<CancellationToken>()).Returns(task);
        _repo.TryReleaseAssignedTaskWithLeaseAsync(task, ReleaseLease.AgentSessionId, 3, Arg.Any<CancellationToken>()).Returns(true);

        var released = await _sut.ReleaseAssignedTaskAsync(7, ReleaseLease, TestContext.Current.CancellationToken);

        Assert.True(released);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseAssignedTaskAsync_AnUnknownTask_ReturnsFalseWithoutTouchingTheRepository()
    {
        _repo.FindTaskAsync(7, Arg.Any<CancellationToken>()).Returns((ServerTask?)null);

        Assert.False(await _sut.ReleaseAssignedTaskAsync(7, ReleaseLease, TestContext.Current.CancellationToken));

        await _repo.DidNotReceiveWithAnyArgs().TryReleaseAssignedTaskWithLeaseAsync(default!, default!, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Pending)]
    [InlineData(TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.Success)]
    public async Task ReleaseAssignedTaskAsync_ATaskThatIsNotAssigned_IsRefused(TaskExecutionStatus status)
    {
        _repo.FindTaskAsync(7, Arg.Any<CancellationToken>()).Returns(new ServerTask { Id = 7, Status = status });

        Assert.False(await _sut.ReleaseAssignedTaskAsync(7, ReleaseLease, TestContext.Current.CancellationToken));

        await _repo.DidNotReceiveWithAnyArgs().TryReleaseAssignedTaskWithLeaseAsync(default!, default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReleaseAssignedTaskAsync_AnAssignedTaskThatAlreadyStarted_IsRefused()
    {
        var task = new ServerTask { Id = 7, Status = TaskExecutionStatus.Assigned, StartedAt = DateTime.UtcNow };
        _repo.FindTaskAsync(7, Arg.Any<CancellationToken>()).Returns(task);

        Assert.False(await _sut.ReleaseAssignedTaskAsync(7, ReleaseLease, TestContext.Current.CancellationToken));

        await _repo.DidNotReceiveWithAnyArgs().TryReleaseAssignedTaskWithLeaseAsync(default!, default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReleaseAssignedTaskAsync_WhenTheLeaseIsNotCurrent_ReturnsFalseAndSavesNothing()
    {
        var task = new ServerTask { Id = 7, Status = TaskExecutionStatus.Assigned };
        _repo.FindTaskAsync(7, Arg.Any<CancellationToken>()).Returns(task);
        _repo.TryReleaseAssignedTaskWithLeaseAsync(task, Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.False(await _sut.ReleaseAssignedTaskAsync(7, ReleaseLease, TestContext.Current.CancellationToken));

        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTaskAsync_CompletedTask_ReturnsFalse()
    {
        var task = new ServerTask { Id = 1, Status = TaskExecutionStatus.Success };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        var result = await _sut.StartTaskAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.False(result);
    }

    [Fact]
    public async Task StartTaskAsync_NotFound_ReturnsFalse()
    {
        _repo.FindTaskAsync(99, Arg.Any<CancellationToken>()).Returns((ServerTask?)null);

        var result = await _sut.StartTaskAsync(99, ct: TestContext.Current.CancellationToken);
        Assert.False(result);
    }

    [Fact]
    public async Task StartTaskAsync_WithPipelineRunId_BroadcastsStepStarted()
    {
        var task = new ServerTask { Id = 1, Status = TaskExecutionStatus.Pending, PipelineRunId = 10, PipelineStepRunId = 20 };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.StartTaskAsync(1, ct: TestContext.Current.CancellationToken);

        _pipelineHub.Clients.Received(1).Group("pipeline-run-10");
    }

    // --- CompleteTaskAsync ---

    [Fact]
    public async Task CompleteTaskAsync_RunningTask_SetsStatusAndBroadcasts()
    {
        var task = new ServerTask { Id = 1, ServerId = 1, Name = "t", Status = TaskExecutionStatus.Running };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.CompleteTaskAsync(1, new TaskResultDto
        {
            Status = TaskExecutionStatus.Success,
            ExitCode = 0,
            Output = "done"
        }, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(TaskExecutionStatus.Success, task.Status);
        Assert.Equal(0, task.ExitCode);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task CompleteTaskAsync_SelfUpdateHandoff_RemainsRunningUntilHeartbeatConfirmation()
    {
        var task = new ServerTask
        {
            Id = 1,
            ServerId = 2,
            Operation = OperationKind.AgentSelfUpdate,
            Status = TaskExecutionStatus.Running,
            EnvironmentVariables = "{\"AETHEUS_UPDATE_DOWNLOAD_URL\":\"sensitive\"}"
        };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        var completed = await _sut.CompleteTaskAsync(
            task.Id,
            new TaskResultDto
            {
                Status = TaskExecutionStatus.Success,
                ExitCode = 0
            },
            TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
        Assert.Null(task.CompletedAt);
        Assert.Equal(TaskEnvProtection.EmptyEnv, task.EnvironmentVariables);
        Assert.Contains(task.Logs, log =>
            log.Message.Contains("awaiting confirmation", StringComparison.Ordinal));
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportDeploymentBuildRefusalAsync_PersistsClassifiedIncidentAndFailsTask()
    {
        var task = new ServerTask
        {
            Id = 17,
            ServerId = 4,
            Name = "build-on-deploy",
            Status = TaskExecutionStatus.Running
        };
        _repo.FindTaskAsync(17, Arg.Any<CancellationToken>()).Returns(task);

        var result = await _sut.ReportDeploymentBuildRefusalAsync(
            17,
            new DeploymentBuildRefusalReport
            {
                IncidentId = Guid.NewGuid(),
                TaskId = 17,
                OccurredAtUtc = DateTime.UtcNow,
                Reason = "deployment-only agent refused a build stage"
            },
            TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(TaskExecutionStatus.Failed, task.Status);
        var incident = Assert.Single(task.Logs);
        Assert.Equal(TaskLogLevel.Error, incident.Level);
        Assert.Contains("[incident:BuildOnDeploymentTarget]", incident.Message, StringComparison.Ordinal);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportDeploymentBuildRefusalAsync_ReplayOfFailedTaskIsIdempotent()
    {
        var task = new ServerTask
        {
            Id = 18,
            ServerId = 4,
            Name = "build-on-deploy",
            Status = TaskExecutionStatus.Failed
        };
        _repo.FindTaskAsync(18, Arg.Any<CancellationToken>()).Returns(task);
        var report = new DeploymentBuildRefusalReport
        {
            IncidentId = Guid.NewGuid(),
            TaskId = 18,
            OccurredAtUtc = DateTime.UtcNow,
            Reason = "deployment-only agent refused a build stage"
        };

        Assert.True(await _sut.ReportDeploymentBuildRefusalAsync(
            18, report, TestContext.Current.CancellationToken));
        Assert.True(await _sut.ReportDeploymentBuildRefusalAsync(
            18, report, TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTaskAsync_StaleFencingToken_DoesNotTransition()
    {
        var task = new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "t",
            Status = TaskExecutionStatus.Running
        };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.TryCompleteTaskWithLeaseAsync(
                task,
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<TaskExecutionStatus>(),
                Arg.Any<int>(),
                Arg.Any<DateTime>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CompleteTaskAsync(
            1,
            new TaskResultDto
            {
                Status = TaskExecutionStatus.Success,
                ExitCode = 0,
                AgentSessionId = "11111111111111111111111111111111",
                AgentSessionFencingToken = 1
            },
            TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTaskAsync_SuccessfulDeploy_ClosesDeployLoop()
    {
        // The deploy task carries the artifact id + app in its (pass-through-encrypted) env.
        var env = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["AETHEUS_DEPLOY_ARTIFACT_ID"] = "42",
            ["AETHEUS_DEPLOY_APP"] = "toto",
            ["AETHEUS_DEPLOY_RELEASE_ID"] = "7"
        });
        var task = new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "Deploy toto",
            Command = "toto",
            Status = TaskExecutionStatus.Running,
            Operation = OperationKind.PipelineDeploy,
            EnvironmentVariables = env
        };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        await _sut.CompleteTaskAsync(1, new TaskResultDto { Status = TaskExecutionStatus.Success, ExitCode = 0 }, ct: TestContext.Current.CancellationToken);

        await _artifactService.Received(1).MarkDeployedAsync(42, "toto", 7, Arg.Any<CancellationToken>());
        Assert.Equal(TaskEnvProtection.EmptyEnv, task.EnvironmentVariables); // secrets still scrubbed
    }

    [Fact]
    public async Task CompleteTaskAsync_SuccessfulDeploy_AnnouncesTheReleaseIsLive()
    {
        var env = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["AETHEUS_DEPLOY_ARTIFACT_ID"] = "42",
            ["AETHEUS_DEPLOY_APP"] = "toto",
            ["AETHEUS_DEPLOY_RELEASE_ID"] = "7"
        });
        var task = new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "Deploy toto",
            Command = "toto",
            Status = TaskExecutionStatus.Running,
            Operation = OperationKind.PipelineDeploy,
            EnvironmentVariables = env,
            PipelineRunId = 12,
            PipelineStepRunId = 13
        };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.FindPipelineStepRunAsync(13, Arg.Any<CancellationToken>())
            .Returns(new PipelineStepRun { Id = 13, PipelineRunId = 12, StageName = "Deploy" });
        _logService.GetTaskOutputVariableLinesAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _artifactService.MarkDeployedAsync(42, "toto", 7, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.CompleteTaskAsync(
            1, new TaskResultDto { Status = TaskExecutionStatus.Success, ExitCode = 0 },
            ct: TestContext.Current.CancellationToken);

        // Carries the stage name because that is what names the target environment in the run's YAML,
        // and is dispatched non-strictly so a post-deployment observer can never fail a live deploy.
        await _domainEvents.Received(1).DispatchAsync(
            Arg.Is<Aetheus.Back.Components.Tasks.ReleaseDeployedEvent>(e =>
                e.ReleaseId == 7 && e.PipelineRunId == 12 && e.StageName == "Deploy" && !e.IsRollback),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTaskAsync_DeployWithoutARelease_AnnouncesNothing()
    {
        var env = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["AETHEUS_DEPLOY_ARTIFACT_ID"] = "42",
            ["AETHEUS_DEPLOY_APP"] = "toto"
        });
        var task = new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "Deploy toto",
            Command = "toto",
            Status = TaskExecutionStatus.Running,
            Operation = OperationKind.PipelineDeploy,
            EnvironmentVariables = env
        };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _artifactService.MarkDeployedAsync(42, "toto", null, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.CompleteTaskAsync(
            1, new TaskResultDto { Status = TaskExecutionStatus.Success, ExitCode = 0 },
            ct: TestContext.Current.CancellationToken);

        await _domainEvents.DidNotReceive().DispatchAsync(
            Arg.Any<Aetheus.Back.Components.Tasks.ReleaseDeployedEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteTaskAsync_DeployClosureFailure_FailsStepAndStillAdvancesRun(bool cancellation)
    {
        var env = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["AETHEUS_DEPLOY_ARTIFACT_ID"] = "42",
            ["AETHEUS_DEPLOY_APP"] = "toto",
            ["AETHEUS_DEPLOY_RELEASE_ID"] = "7"
        });
        var task = new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "Deploy toto",
            Command = "toto",
            Status = TaskExecutionStatus.Running,
            Operation = OperationKind.PipelineDeploy,
            EnvironmentVariables = env,
            PipelineRunId = 12,
            PipelineStepRunId = 13
        };
        var step = new PipelineStepRun { Id = 13, PipelineRunId = 12, StageName = "Deploy" };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.FindPipelineStepRunAsync(13, Arg.Any<CancellationToken>()).Returns(step);
        _logService.GetTaskOutputVariableLinesAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _artifactService.MarkDeployedAsync(42, "toto", 7, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw (cancellation
                ? new OperationCanceledException("request disconnected")
                : new InvalidOperationException("release link mismatch")));

        var completed = await _sut.CompleteTaskAsync(
            1, new TaskResultDto { Status = TaskExecutionStatus.Success, ExitCode = 0 },
            ct: TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Equal(TaskExecutionStatus.Failed, task.Status);
        Assert.Equal(-1, task.ExitCode);
        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        Assert.Equal(-1, step.ExitCode);
        Assert.Equal(TaskEnvProtection.EmptyEnv, task.EnvironmentVariables);
        await _repo.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(CancellationToken.None);
        await _domainEvents.Received(1).DispatchStrictAsync(
            Arg.Is<PipelineStepTaskCompletedEvent>(e => e.PipelineRunId == 12 && e.StageName == "Deploy"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTaskAsync_FailedDeploy_DoesNotCloseDeployLoop()
    {
        var env = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["AETHEUS_DEPLOY_ARTIFACT_ID"] = "42" });
        var task = new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "Deploy toto",
            Command = "toto",
            Status = TaskExecutionStatus.Running,
            Operation = OperationKind.PipelineDeploy,
            EnvironmentVariables = env
        };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        await _sut.CompleteTaskAsync(1, new TaskResultDto { Status = TaskExecutionStatus.Failed, ExitCode = 1 }, ct: TestContext.Current.CancellationToken);

        await _artifactService.DidNotReceive().MarkDeployedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Run unit tests", "Build", TaskFailureCodes.TestsFailed)]
    [InlineData("Compile application", "Test", TaskFailureCodes.BuildFailed)]
    [InlineData("Invoke scanner", "Quality", TaskFailureCodes.ToolError)]
    [InlineData("Contest deployment", "Quality", TaskFailureCodes.ToolError)]
    [InlineData("Repackageable artifact", "Quality", TaskFailureCodes.ToolError)]
    public async Task CompleteTaskAsync_PipelineFailure_ClassifiesVisibleFailure(
        string taskName,
        string groupName,
        string expectedCode)
    {
        var task = new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = taskName,
            Status = TaskExecutionStatus.Running,
            PipelineRunId = 12,
            PipelineStepRunId = 13
        };
        var step = new PipelineStepRun
        {
            Id = 13,
            PipelineRunId = 12,
            StageName = groupName,
            StepName = taskName,
            GroupName = groupName
        };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.FindPipelineStepRunAsync(13, Arg.Any<CancellationToken>()).Returns(step);
        _logService.GetTaskOutputVariableLinesAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        var completed = await _sut.CompleteTaskAsync(
            1,
            new TaskResultDto { Status = TaskExecutionStatus.Failed, ExitCode = 1 },
            ct: TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Equal(expectedCode, task.FailureCode);
        Assert.Contains(task.Logs, log => log.Message.Contains($"[incident:{expectedCode}]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteTaskAsync_AFailedActionOutsideAnyPipeline_IsNotAnIncident()
    {
        // Repeated failed service-start attempts are distinct server incidents.
        var task = new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "Service Start - dovecot",
            Status = TaskExecutionStatus.Running
        };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _logService.GetTaskOutputVariableLinesAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        var completed = await _sut.CompleteTaskAsync(
            1,
            new TaskResultDto { Status = TaskExecutionStatus.Failed, ExitCode = 5 },
            ct: TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Equal(TaskExecutionStatus.Failed, task.Status);
        Assert.Equal(5, task.ExitCode);
        Assert.Null(task.FailureCode);
        Assert.DoesNotContain(task.Logs, log => log.Message.Contains("[incident:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteTaskAsync_SuccessfulNonDeploy_DoesNotCloseDeployLoop()
    {
        var task = new ServerTask { Id = 1, ServerId = 1, Name = "t", Status = TaskExecutionStatus.Running, Operation = OperationKind.None };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        await _sut.CompleteTaskAsync(1, new TaskResultDto { Status = TaskExecutionStatus.Success, ExitCode = 0 }, ct: TestContext.Current.CancellationToken);

        await _artifactService.DidNotReceive().MarkDeployedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTaskAsync_NotRunning_ReturnsFalse()
    {
        var task = new ServerTask { Id = 1, Status = TaskExecutionStatus.Pending };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        var result = await _sut.CompleteTaskAsync(1, new TaskResultDto { Status = TaskExecutionStatus.Success }, ct: TestContext.Current.CancellationToken);
        Assert.False(result);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Cancelled)]
    [InlineData(TaskExecutionStatus.Timeout)]
    public async Task CompleteTaskAsync_AssignedTask_AcceptsOnlyTerminalFailure(TaskExecutionStatus status)
    {
        var task = new ServerTask { Id = 1, ServerId = 1, Name = "unsupported", Status = TaskExecutionStatus.Assigned };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        var completed = await _sut.CompleteTaskAsync(1, new TaskResultDto { Status = status, ExitCode = -1 }, ct: TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Equal(status, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task CompleteTaskAsync_AssignedTask_RejectsSuccess()
    {
        var task = new ServerTask { Id = 1, Status = TaskExecutionStatus.Assigned };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        var completed = await _sut.CompleteTaskAsync(1, new TaskResultDto { Status = TaskExecutionStatus.Success }, ct: TestContext.Current.CancellationToken);

        Assert.False(completed);
        Assert.Equal(TaskExecutionStatus.Assigned, task.Status);
    }

    [Fact]
    public async Task CompleteTaskAsync_RunningTask_RejectsNonTerminalStatus()
    {
        var task = new ServerTask { Id = 1, Status = TaskExecutionStatus.Running };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        var completed = await _sut.CompleteTaskAsync(1, new TaskResultDto { Status = TaskExecutionStatus.Running }, ct: TestContext.Current.CancellationToken);

        Assert.False(completed);
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
    }

    [Fact]
    public async Task CompleteTaskAsync_NotFound_ReturnsFalse()
    {
        _repo.FindTaskAsync(99, Arg.Any<CancellationToken>()).Returns((ServerTask?)null);

        var result = await _sut.CompleteTaskAsync(99, new TaskResultDto { Status = TaskExecutionStatus.Success }, ct: TestContext.Current.CancellationToken);
        Assert.False(result);
    }

    [Fact]
    public async Task CompleteTaskAsync_WithPipelineStepRun_UpdatesStep()
    {
        var task = new ServerTask { Id = 1, ServerId = 1, Name = "t", Status = TaskExecutionStatus.Running, PipelineRunId = 10, PipelineStepRunId = 20 };
        var stepRun = new PipelineStepRun { Id = 20, PipelineRunId = 10, StepName = "build", StageName = "build", Order = 1, Status = TaskExecutionStatus.Running };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.FindPipelineStepRunAsync(20, Arg.Any<CancellationToken>()).Returns(stepRun);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _logService.GetTaskOutputVariableLinesAsync(1, Arg.Any<CancellationToken>()).Returns(new List<string>());


        await _sut.CompleteTaskAsync(1, new TaskResultDto
        {
            Status = TaskExecutionStatus.Success,
            ExitCode = 0
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Success, stepRun.Status);
        Assert.NotNull(stepRun.CompletedAt);
        await _domainEvents.Received(1).DispatchStrictAsync(
            Arg.Is<PipelineStepTaskCompletedEvent>(e => e.PipelineRunId == 10 && e.StageName == "build"),
            Arg.Any<CancellationToken>());
    }

    // --- CancelTaskAsync ---

    [Fact]
    public async Task CancelTaskAsync_PendingTask_Cancels()
    {
        var task = new ServerTask { Id = 1, Status = TaskExecutionStatus.Pending };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.CancelTaskAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(TaskExecutionStatus.Cancelled, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task CancelTaskAsync_CompletedTask_ReturnsFalse()
    {
        var task = new ServerTask { Id = 1, Status = TaskExecutionStatus.Success };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        var result = await _sut.CancelTaskAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.False(result);
    }

    [Fact]
    public async Task CancelTaskAsync_NotFound_ReturnsFalse()
    {
        _repo.FindTaskAsync(99, Arg.Any<CancellationToken>()).Returns((ServerTask?)null);

        var result = await _sut.CancelTaskAsync(99, ct: TestContext.Current.CancellationToken);
        Assert.False(result);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Cancelled)]
    [InlineData(TaskExecutionStatus.Timeout)]
    public async Task CancelTaskAsync_AlreadyTerminal_ReturnsFalse_NoReAdvance(TaskExecutionStatus terminal)
    {
        // A task already in ANY terminal state (Cancelled/Timeout, not just Success/Failed) must not
        // be re-cancelled: doing so would re-broadcast StepCompleted and re-enter AdvanceStageAsync,
        // double-advancing the pipeline stage (e.g. a manual cancel racing a TaskTimeoutService sweep).
        var task = new ServerTask { Id = 1, Status = terminal, PipelineRunId = 5, PipelineStepRunId = 20 };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        var result = await _sut.CancelTaskAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        await _domainEvents.DidNotReceive().DispatchStrictAsync(
            Arg.Any<PipelineStepTaskCompletedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelTaskAsync_StepAlreadyCompleted_DoesNotReBroadcastOrAdvance()
    {
        // Race: the step's task is being cancelled while a concurrent CompleteTaskAsync already
        // set the step terminal. This cancel must update the task but NOT re-advance the stage.
        var task = new ServerTask { Id = 1, Status = TaskExecutionStatus.Running, PipelineRunId = 5, PipelineStepRunId = 20 };
        var stepRun = new PipelineStepRun { Id = 20, PipelineRunId = 5, StageName = "build", Status = TaskExecutionStatus.Success, CompletedAt = DateTime.UtcNow };
        _repo.FindTaskAsync(1, Arg.Any<CancellationToken>()).Returns(task);
        _repo.FindPipelineStepRunAsync(20, Arg.Any<CancellationToken>()).Returns(stepRun);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.CancelTaskAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result); // the task itself was cancelled
        Assert.Equal(TaskExecutionStatus.Success, stepRun.Status); // step untouched (already terminal)
        await _domainEvents.DidNotReceive().DispatchStrictAsync(
            Arg.Any<PipelineStepTaskCompletedEvent>(), Arg.Any<CancellationToken>());
    }
}
