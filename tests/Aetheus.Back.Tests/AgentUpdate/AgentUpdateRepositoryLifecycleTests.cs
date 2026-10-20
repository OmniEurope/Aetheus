// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.AgentUpdate;

/// <summary>
/// The reservation → handoff → confirmation lifecycle of an agent self-update, and the two ways it
/// can end badly: a rejected heartbeat (wrong version, unsupported protocol, lost capability) and an
/// expiry sweep. The transaction and row-lock branches are relational-only and stay out of scope
/// here by design; <see cref="AgentUpdateRepositoryTests"/> covers the queueing decision.
/// </summary>
public sealed class AgentUpdateRepositoryLifecycleTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly AppDbContext _db;
    private readonly AgentUpdateRepository _repository;

    public AgentUpdateRepositoryLifecycleTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AppDbContext(options, _clock);
        _repository = new AgentUpdateRepository(_db, _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SaveAsync() => await _db.SaveChangesAsync(Ct);

    private static AgentReleaseManifestDto Release(string version = "1.0.926") =>
        new()
        {
            SoftwareVersion = version,
            ProtocolVersion = AgentProtocol.CurrentVersion,
            MinimumSupportedProtocol = AgentProtocol.CurrentVersion,
            MaximumSupportedProtocol = AgentProtocol.CurrentVersion,
            Commit = "deadbeef"
        };

    private Server AddServer(
        int id = 1, string name = "runner", string version = "1.0.900",
        bool runner = false, bool deploymentTarget = false, string? sessionId = "session-old")
    {
        var server = new Server
        {
            Id = id,
            Name = name,
            Hostname = name,
            AgentVersion = version,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentSessionId = sessionId,
            PipelineRunnerEnabled = runner,
            DeploymentTargetAvailable = deploymentTarget
        };
        _db.Servers.Add(server);
        return server;
    }

    private AgentUpdateRequest AddRequest(
        Server server,
        AgentUpdateRequestStatus status,
        bool isActive = true,
        string targetVersion = "1.0.926",
        DateTime? requestedAt = null,
        DateTime? startedAt = null,
        DateTime? handoffAt = null,
        DateTime? confirmationDeadline = null,
        IEnumerable<string>? expectedCapabilities = null,
        ServerTask? task = null,
        int id = 10)
    {
        var request = new AgentUpdateRequest
        {
            Id = id,
            ServerId = server.Id,
            Server = server,
            TargetVersion = targetVersion,
            ObservedVersion = server.AgentVersion,
            ObservedProtocolVersion = server.AgentProtocolVersion,
            ObservedSessionId = server.AgentSessionId,
            RequestedBy = "test",
            RequestedAt = requestedAt ?? Now.UtcDateTime,
            StartedAt = startedAt,
            HandoffAt = handoffAt,
            ConfirmationDeadline = confirmationDeadline,
            Status = status,
            IsActive = isActive,
            ExpectedCapabilitiesJson = JsonSerializer.Serialize(
                expectedCapabilities?.ToList()
                ?? [AgentCapabilities.SelfUpdate, AgentCapabilities.ShellExecution]),
            Task = task,
            TaskId = task?.Id
        };
        if (task is not null) _db.Tasks.Add(task);
        _db.AgentUpdateRequests.Add(request);
        return request;
    }

    private static ServerTask UpdateTask(
        int id = 50, TaskExecutionStatus status = TaskExecutionStatus.Running, int serverId = 1) =>
        new()
        {
            Id = id,
            ServerId = serverId,
            Name = "Agent self-update",
            Operation = OperationKind.AgentSelfUpdate,
            Status = status,
            EnvironmentVariables = "{\"AETHEUS_AGENT_TARGET_VERSION\":\"1.0.926\"}"
        };

    private static ServerHeartbeatDto Heartbeat(
        string? sessionId = "session-new",
        string? version = "1.0.926",
        int? protocol = AgentProtocol.CurrentVersion,
        IEnumerable<string>? capabilities = null) =>
        new()
        {
            AgentSessionId = sessionId,
            AgentVersion = version,
            AgentProtocolVersion = protocol,
            AgentCapabilities = (capabilities
                ?? [AgentCapabilities.SelfUpdate, AgentCapabilities.ShellExecution]).ToList()
        };

    // ---------- own-reads over Server ----------

    [Fact]
    public async Task GetServersForAgentUpdateAsync_WithoutAScopeReturnsTheWholeFleetOrdered()
    {
        AddServer(1, "zeta");
        AddServer(2, "alpha");
        await SaveAsync();

        var servers = await _repository.GetServersForAgentUpdateAsync(null, Ct);

        Assert.Equal(["alpha", "zeta"], servers.Select(server => server.Name));
    }

    [Fact]
    public async Task FindServerAsync_ReturnsATrackedServerOrNull()
    {
        AddServer(1, "runner");
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var server = await _repository.FindServerAsync(1, Ct);
        server!.Name = "renamed";
        await SaveAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal("renamed", (await _repository.FindServerAsync(1, Ct))!.Name);
        Assert.Null(await _repository.FindServerAsync(404, Ct));
    }

    [Fact]
    public async Task GetServersForCompatibilityAsync_ProjectsTheFactsAndParsesTheCapabilitiesJson()
    {
        var server = AddServer(1, "runner", runner: true, deploymentTarget: true);
        server.Status = ServerStatus.Online;
        server.LastHeartbeat = Now.UtcDateTime;
        server.AgentCapabilitiesJson = JsonSerializer.Serialize(
            new[] { AgentCapabilities.SelfUpdate, AgentCapabilities.ShellExecution });
        AddServer(2, "other");
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var facts = await _repository.GetServersForCompatibilityAsync([1], Ct);

        var only = Assert.Single(facts);
        Assert.Equal(1, only.Id);
        Assert.Equal("1.0.900", only.AgentVersion);
        Assert.Equal(AgentProtocol.CurrentVersion, only.AgentProtocolVersion);
        Assert.Equal(ServerStatus.Online, only.Status);
        Assert.True(only.PipelineRunnerEnabled);
        Assert.True(only.DeploymentTargetAvailable);
        Assert.Equal(Now.UtcDateTime, only.LastHeartbeat);
        Assert.Contains(AgentCapabilities.SelfUpdate, only.AgentCapabilities);
    }

    [Fact]
    public async Task GetServersForCompatibilityAsync_WithoutAScopeCoversTheWholeFleet()
    {
        AddServer(1, "one");
        AddServer(2, "two");
        await SaveAsync();

        Assert.Equal(2, (await _repository.GetServersForCompatibilityAsync(null, Ct)).Count);
    }

    [Fact]
    public async Task CountActiveNonUpdateTasksAsync_IgnoresSelfUpdatesFinishedWorkAndOtherServers()
    {
        AddServer(1, "runner");
        _db.Tasks.AddRange(
            new ServerTask { Id = 1, ServerId = 1, Status = TaskExecutionStatus.Pending },
            new ServerTask { Id = 2, ServerId = 1, Status = TaskExecutionStatus.Assigned },
            new ServerTask { Id = 3, ServerId = 1, Status = TaskExecutionStatus.Running },
            new ServerTask { Id = 4, ServerId = 1, Status = TaskExecutionStatus.Success },
            new ServerTask
            {
                Id = 5,
                ServerId = 1,
                Status = TaskExecutionStatus.Running,
                Operation = OperationKind.AgentSelfUpdate
            },
            new ServerTask { Id = 6, ServerId = 2, Status = TaskExecutionStatus.Running });
        await SaveAsync();

        Assert.Equal(3, await _repository.CountActiveNonUpdateTasksAsync(1, Ct));
    }

    // ---------- reservation ----------

    [Fact]
    public async Task ReserveAsync_CreatesTheRequestAndMarksTheServerReserved()
    {
        var server = AddServer(1, "runner", runner: true, deploymentTarget: true);
        await SaveAsync();

        var (request, created) = await _repository.ReserveAsync(server, Release(), "alice", Ct);

        Assert.True(created);
        Assert.Equal(AgentUpdateRequestStatus.WaitingForIdle, request.Status);
        Assert.True(request.IsActive);
        Assert.Equal("alice", request.RequestedBy);
        Assert.Equal("1.0.926", request.TargetVersion);
        Assert.Equal("1.0.900", request.ObservedVersion);
        Assert.Equal("session-old", request.ObservedSessionId);
        Assert.Equal(Now.UtcDateTime, request.RequestedAt);
        Assert.True(server.AgentUpdateReserved);
        Assert.Equal(Now.UtcDateTime, server.AgentUpdateReservedAt);
    }

    [Fact]
    public async Task ReserveAsync_SnapshotsTheCapabilitiesTheNewBinaryMustStillReport()
    {
        var server = AddServer(1, "runner", runner: true, deploymentTarget: true);
        await SaveAsync();

        var (request, _) = await _repository.ReserveAsync(server, Release(), "alice", Ct);

        Assert.Equal(
            [
                AgentCapabilities.SelfUpdate,
                AgentCapabilities.Deployment,
                AgentCapabilities.DeploymentFunctions,
                AgentCapabilities.PipelineBuild,
                AgentCapabilities.PipelineFunctions,
                AgentCapabilities.ShellExecution
            ],
            JsonSerializer.Deserialize<List<string>>(request.ExpectedCapabilitiesJson)!);
    }

    [Fact]
    public async Task ReserveAsync_OmitsTheRunnerAndDeploymentCapabilitiesOfAPlainServer()
    {
        var server = AddServer(1, "runner");
        await SaveAsync();

        var (request, _) = await _repository.ReserveAsync(server, Release(), "alice", Ct);

        Assert.Equal(
            [AgentCapabilities.SelfUpdate, AgentCapabilities.ShellExecution],
            JsonSerializer.Deserialize<List<string>>(request.ExpectedCapabilitiesJson)!.Order());
    }

    [Fact]
    public async Task ReserveAsync_ReturnsTheExistingActiveRequestForTheSameTargetVersion()
    {
        var server = AddServer(1, "runner");
        var existing = AddRequest(server, AgentUpdateRequestStatus.Queued);
        await SaveAsync();

        var (request, created) = await _repository.ReserveAsync(server, Release(), "bob", Ct);

        Assert.False(created);
        Assert.Equal(existing.Id, request.Id);
        Assert.Equal("test", request.RequestedBy);
        Assert.Equal(1, await _db.AgentUpdateRequests.CountAsync(Ct));
    }

    [Fact]
    public async Task ReserveAsync_StartsAFreshRequestWhenTheOnlyMatchIsNoLongerActive()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Failed, isActive: false);
        await SaveAsync();

        var (_, created) = await _repository.ReserveAsync(server, Release(), "bob", Ct);

        Assert.True(created);
        Assert.Equal(2, await _db.AgentUpdateRequests.CountAsync(Ct));
    }

    [Fact]
    public async Task ReserveAsync_StartsAFreshRequestForADifferentTargetVersion()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.WaitingForIdle, targetVersion: "1.0.900");
        await SaveAsync();

        var (request, created) = await _repository.ReserveAsync(server, Release("1.0.926"), "bob", Ct);

        Assert.True(created);
        Assert.Equal("1.0.926", request.TargetVersion);
    }

    // ---------- scheduler reads ----------

    [Fact]
    public async Task GetWaitingRequestIdsAsync_ReturnsOnlyActiveWaitersOldestFirst()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.WaitingForIdle,
            requestedAt: Now.UtcDateTime.AddMinutes(5), id: 1);
        AddRequest(server, AgentUpdateRequestStatus.WaitingForIdle,
            requestedAt: Now.UtcDateTime, id: 2);
        AddRequest(server, AgentUpdateRequestStatus.Queued, id: 3);
        AddRequest(server, AgentUpdateRequestStatus.WaitingForIdle, isActive: false, id: 4);
        await SaveAsync();

        Assert.Equal([2, 1], await _repository.GetWaitingRequestIdsAsync(Ct));
    }

    [Fact]
    public async Task FindActiveByServerAsync_ReturnsTheNewestActiveRequestOfThatServer()
    {
        var server = AddServer(1, "runner");
        var other = AddServer(2, "other");
        AddRequest(server, AgentUpdateRequestStatus.Queued, requestedAt: Now.UtcDateTime, id: 1);
        AddRequest(server, AgentUpdateRequestStatus.Downloading,
            requestedAt: Now.UtcDateTime.AddMinutes(5), id: 2);
        AddRequest(server, AgentUpdateRequestStatus.Failed, isActive: false,
            requestedAt: Now.UtcDateTime.AddMinutes(10), id: 3);
        AddRequest(other, AgentUpdateRequestStatus.Queued,
            requestedAt: Now.UtcDateTime.AddMinutes(20), id: 4);
        await SaveAsync();

        Assert.Equal(2, (await _repository.FindActiveByServerAsync(1, Ct))!.Id);
        Assert.Null(await _repository.FindActiveByServerAsync(3, Ct));
    }

    [Fact]
    public async Task TryQueueAsync_IsANoOpOnceTheTaskExistsOrTheRequestIsClosed()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Queued, task: UpdateTask(), id: 1);
        AddRequest(server, AgentUpdateRequestStatus.WaitingForIdle, isActive: false, id: 2);
        await SaveAsync();

        var queued = await _repository.TryQueueAsync(1, Ct);
        var closed = await _repository.TryQueueAsync(2, Ct);

        Assert.Null(queued.CreatedTask);
        Assert.Equal(0, queued.BlockingTaskCount);
        Assert.Null(closed.CreatedTask);
        Assert.Equal(1, await _db.Tasks.CountAsync(Ct));
    }

    [Fact]
    public async Task TryQueueAsync_CarriesTheTargetVersionIntoTheTaskEnvironment()
    {
        var server = AddServer(1, "runner");
        server.AgentUpdateReserved = true;
        server.AgentUpdateReservedAt = Now.UtcDateTime;
        AddRequest(server, AgentUpdateRequestStatus.WaitingForIdle, id: 1);
        await SaveAsync();

        var result = await _repository.TryQueueAsync(1, Ct);

        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(
            result.CreatedTask!.EnvironmentVariables)!;
        Assert.Equal("1.0.926", environment["AETHEUS_AGENT_TARGET_VERSION"]);
        Assert.Equal(ExecutorType.Operation, result.CreatedTask.Executor);
        Assert.Equal(result.CreatedTask.Id, result.Request.TaskId);
        Assert.Equal(Now.UtcDateTime, result.Request.StartedAt);
    }

    // ---------- progress ----------

    [Theory]
    [InlineData(AgentUpdatePhase.Downloading, AgentUpdateRequestStatus.Downloading)]
    [InlineData(AgentUpdatePhase.Downloaded, AgentUpdateRequestStatus.Staging)]
    [InlineData(AgentUpdatePhase.Extracting, AgentUpdateRequestStatus.Staging)]
    [InlineData(AgentUpdatePhase.LaunchingUpdater, AgentUpdateRequestStatus.Handoff)]
    [InlineData(AgentUpdatePhase.AgentOffline, AgentUpdateRequestStatus.Handoff)]
    [InlineData(AgentUpdatePhase.Done, AgentUpdateRequestStatus.Handoff)]
    [InlineData(AgentUpdatePhase.Failed, AgentUpdateRequestStatus.Failed)]
    public async Task MarkProgressAsync_MapsEachPhaseOntoTheRequestStatus(
        AgentUpdatePhase phase, AgentUpdateRequestStatus expected)
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Queued);
        await SaveAsync();

        var request = await _repository.MarkProgressAsync(
            1, new AgentUpdateProgressDto { ServerId = 1, Phase = phase }, Ct);

        Assert.Equal(expected, request!.Status);
    }

    [Fact]
    public async Task MarkProgressAsync_LeavesTheStatusAloneForAPhaseWithNoMapping()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Downloading);
        await SaveAsync();

        var request = await _repository.MarkProgressAsync(
            1,
            new AgentUpdateProgressDto { ServerId = 1, Phase = AgentUpdatePhase.PickedUp },
            Ct);

        Assert.Equal(AgentUpdateRequestStatus.Downloading, request!.Status);
        Assert.Null(request.HandoffAt);
    }

    [Fact]
    public async Task MarkProgressAsync_StampsTheConfirmationDeadlineOnTheFirstHandoffOnly()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Staging);
        await SaveAsync();

        var first = await _repository.MarkProgressAsync(
            1,
            new AgentUpdateProgressDto { ServerId = 1, Phase = AgentUpdatePhase.LaunchingUpdater },
            Ct);
        var stampedAt = first!.HandoffAt;

        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = await _repository.MarkProgressAsync(
            1,
            new AgentUpdateProgressDto { ServerId = 1, Phase = AgentUpdatePhase.AgentOffline },
            Ct);

        Assert.Equal(Now.UtcDateTime, stampedAt);
        Assert.Equal(stampedAt, second!.HandoffAt);
        Assert.Equal(Now.UtcDateTime.AddMinutes(5), second.ConfirmationDeadline);
    }

    [Fact]
    public async Task MarkProgressAsync_OnFailureClosesTheRequestAndReleasesTheReservation()
    {
        var server = AddServer(1, "runner");
        server.AgentUpdateReserved = true;
        server.AgentUpdateReservedAt = Now.UtcDateTime;
        AddRequest(server, AgentUpdateRequestStatus.Downloading);
        await SaveAsync();

        var request = await _repository.MarkProgressAsync(
            1,
            new AgentUpdateProgressDto
            {
                ServerId = 1,
                Phase = AgentUpdatePhase.Failed,
                Message = "tar exit code 2"
            },
            Ct);

        Assert.False(request!.IsActive);
        Assert.Equal("agent-update-failed", request.FailureCode);
        Assert.Equal("tar exit code 2", request.FailureDiagnostic);
        Assert.False(server.AgentUpdateReserved);
        Assert.Null(server.AgentUpdateReservedAt);
    }

    [Fact]
    public async Task MarkProgressAsync_ReturnsNullWhenNoActiveRequestExists()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Failed, isActive: false);
        await SaveAsync();

        Assert.Null(await _repository.MarkProgressAsync(
            1,
            new AgentUpdateProgressDto { ServerId = 1, Phase = AgentUpdatePhase.Downloading },
            Ct));
    }

    // ---------- confirmation ----------

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_ConfirmsTheUpdateAndCompletesItsTask()
    {
        var server = AddServer(1, "runner");
        server.AgentUpdateReserved = true;
        server.AgentUpdateReservedAt = Now.UtcDateTime;
        AddRequest(server, AgentUpdateRequestStatus.Handoff, task: UpdateTask());
        await SaveAsync();

        var request = await _repository.ConfirmFromHeartbeatAsync(1, Heartbeat(), Ct);

        Assert.Equal(AgentUpdateRequestStatus.Confirmed, request!.Status);
        Assert.False(request.IsActive);
        Assert.Equal(Now.UtcDateTime, request.ConfirmedAt);
        Assert.Equal("session-new", request.ConfirmedSessionId);
        Assert.False(server.AgentUpdateReserved);
        Assert.Equal(TaskExecutionStatus.Success, request.Task!.Status);
        Assert.Equal(0, request.Task.ExitCode);
        Assert.Equal(TaskEnvProtection.EmptyEnv, request.Task.EnvironmentVariables);
    }

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_IgnoresARequestThatHasNotHandedOffYet()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Downloading);
        await SaveAsync();

        Assert.Null(await _repository.ConfirmFromHeartbeatAsync(1, Heartbeat(), Ct));
    }

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_IgnoresAHeartbeatFromTheUnchangedSession()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Handoff);
        await SaveAsync();

        Assert.Null(await _repository.ConfirmFromHeartbeatAsync(1, Heartbeat(sessionId: "session-old"), Ct));
        Assert.Null(await _repository.ConfirmFromHeartbeatAsync(1, Heartbeat(sessionId: null), Ct));
    }

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_KeepsWaitingWhenSystemdRestartedTheOldBinary()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Handoff);
        await SaveAsync();

        var outcome = await _repository.ConfirmFromHeartbeatAsync(
            1, Heartbeat(version: "1.0.900"), Ct);

        Assert.Null(outcome);
        Assert.Equal(
            AgentUpdateRequestStatus.Handoff,
            (await _db.AgentUpdateRequests.AsNoTracking().FirstAsync(Ct)).Status);
    }

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_FailsOnAVersionThatIsNeitherTheOldNorTheTargetOne()
    {
        var server = AddServer(1, "runner");
        server.AgentUpdateReserved = true;
        AddRequest(server, AgentUpdateRequestStatus.Handoff, task: UpdateTask());
        await SaveAsync();

        var request = await _repository.ConfirmFromHeartbeatAsync(1, Heartbeat(version: "9.9.9"), Ct);

        Assert.Equal(AgentUpdateRequestStatus.Failed, request!.Status);
        Assert.Equal("wrong-version", request.FailureCode);
        Assert.Contains("1.0.926", request.FailureDiagnostic);
        Assert.False(request.IsActive);
        Assert.False(server.AgentUpdateReserved);
        Assert.Equal(TaskExecutionStatus.Failed, request.Task!.Status);
        Assert.Equal(-1, request.Task.ExitCode);
    }

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_ReportsAMissingVersionRatherThanCrashing()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Handoff);
        await SaveAsync();

        var request = await _repository.ConfirmFromHeartbeatAsync(1, Heartbeat(version: null), Ct);

        Assert.Equal("wrong-version", request!.FailureCode);
        Assert.Contains("missing", request.FailureDiagnostic);
    }

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_FailsOnAnUnsupportedOrAbsentProtocol()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Handoff, id: 10);
        await SaveAsync();

        var unsupported = await _repository.ConfirmFromHeartbeatAsync(
            1, Heartbeat(protocol: AgentProtocol.MaximumSupportedVersion + 1), Ct);

        Assert.Equal("unsupported-protocol", unsupported!.FailureCode);
        Assert.Contains((AgentProtocol.MaximumSupportedVersion + 1).ToString(), unsupported.FailureDiagnostic);
    }

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_ReportsAnAbsentProtocolAsMissing()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Handoff);
        await SaveAsync();

        var request = await _repository.ConfirmFromHeartbeatAsync(1, Heartbeat(protocol: null), Ct);

        Assert.Equal("unsupported-protocol", request!.FailureCode);
        Assert.Contains("missing", request.FailureDiagnostic);
    }

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_FailsWhenTheNewBinaryLostAnExpectedCapability()
    {
        var server = AddServer(1, "runner");
        AddRequest(
            server,
            AgentUpdateRequestStatus.Handoff,
            expectedCapabilities:
            [
                AgentCapabilities.SelfUpdate,
                AgentCapabilities.ShellExecution,
                AgentCapabilities.Deployment
            ]);
        await SaveAsync();

        var request = await _repository.ConfirmFromHeartbeatAsync(
            1,
            Heartbeat(capabilities: [AgentCapabilities.SelfUpdate, AgentCapabilities.ShellExecution]),
            Ct);

        Assert.Equal("missing-capabilities", request!.FailureCode);
        Assert.Contains(AgentCapabilities.Deployment, request.FailureDiagnostic);
    }

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_TreatsAnAbsentCapabilityListAsReportingNothing()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Handoff);
        await SaveAsync();

        var request = await _repository.ConfirmFromHeartbeatAsync(
            1, Heartbeat(capabilities: null) with { AgentCapabilities = null }, Ct);

        Assert.Equal("missing-capabilities", request!.FailureCode);
    }

    [Fact]
    public async Task ConfirmFromHeartbeatAsync_AcceptsExtraCapabilitiesTheNewBinaryGained()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.Handoff);
        await SaveAsync();

        var request = await _repository.ConfirmFromHeartbeatAsync(
            1,
            Heartbeat(capabilities:
            [
                AgentCapabilities.SelfUpdate,
                AgentCapabilities.ShellExecution,
                AgentCapabilities.Analysis
            ]),
            Ct);

        Assert.Equal(AgentUpdateRequestStatus.Confirmed, request!.Status);
    }

    // ---------- expiry sweep ----------

    [Fact]
    public async Task FailExpiredAsync_TimesOutAConfirmationThatNeverArrived()
    {
        var server = AddServer(1, "runner");
        server.AgentUpdateReserved = true;
        server.AgentUpdateReservedAt = Now.UtcDateTime;
        AddRequest(
            server,
            AgentUpdateRequestStatus.Handoff,
            handoffAt: Now.UtcDateTime.AddMinutes(-10),
            confirmationDeadline: Now.UtcDateTime.AddMinutes(-5),
            task: UpdateTask(status: TaskExecutionStatus.Running));
        await SaveAsync();

        var expired = await _repository.FailExpiredAsync(Ct);

        var request = Assert.Single(expired);
        Assert.Equal(AgentUpdateRequestStatus.Failed, request.Status);
        Assert.Equal("confirmation-timeout", request.FailureCode);
        Assert.Equal(TaskExecutionStatus.Timeout, request.Task!.Status);
        Assert.Equal(-1, request.Task.ExitCode);
        Assert.False(server.AgentUpdateReserved);
    }

    [Fact]
    public async Task FailExpiredAsync_CancelsAnUpdateThatNeverReachedHandoff()
    {
        var server = AddServer(1, "runner");
        AddRequest(
            server,
            AgentUpdateRequestStatus.Downloading,
            startedAt: Now.UtcDateTime.AddSeconds(-601),
            task: UpdateTask(status: TaskExecutionStatus.Assigned));
        await SaveAsync();

        var request = Assert.Single(await _repository.FailExpiredAsync(Ct));

        Assert.Equal("update-task-timeout", request.FailureCode);
        Assert.Equal(TaskExecutionStatus.Cancelled, request.Task!.Status);
        Assert.Equal(TaskEnvProtection.EmptyEnv, request.Task.EnvironmentVariables);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Timeout)]
    [InlineData(TaskExecutionStatus.Cancelled)]
    public async Task FailExpiredAsync_FollowsATerminalTaskOfARequestStillBeforeHandoff(
        TaskExecutionStatus taskStatus)
    {
        var server = AddServer(1, "runner");
        AddRequest(
            server,
            AgentUpdateRequestStatus.Queued,
            startedAt: Now.UtcDateTime,
            task: UpdateTask(status: taskStatus));
        await SaveAsync();

        var request = Assert.Single(await _repository.FailExpiredAsync(Ct));

        Assert.Equal("update-task-timeout", request.FailureCode);
        Assert.Equal(taskStatus, request.Task!.Status);
    }

    [Fact]
    public async Task FailExpiredAsync_DoesNotFollowATerminalTaskOnceTheAgentHasHandedOff()
    {
        var server = AddServer(1, "runner");
        AddRequest(
            server,
            AgentUpdateRequestStatus.Handoff,
            handoffAt: Now.UtcDateTime,
            confirmationDeadline: Now.UtcDateTime.AddMinutes(5),
            task: UpdateTask(status: TaskExecutionStatus.Failed));
        await SaveAsync();

        Assert.Empty(await _repository.FailExpiredAsync(Ct));
    }

    [Fact]
    public async Task FailExpiredAsync_LeavesWaitersAndFreshUpdatesAlone()
    {
        var server = AddServer(1, "runner");
        AddRequest(server, AgentUpdateRequestStatus.WaitingForIdle, id: 1);
        AddRequest(server, AgentUpdateRequestStatus.Downloading, startedAt: Now.UtcDateTime, id: 2);
        AddRequest(
            server,
            AgentUpdateRequestStatus.Handoff,
            handoffAt: Now.UtcDateTime,
            confirmationDeadline: Now.UtcDateTime.AddMinutes(5),
            id: 3);
        AddRequest(server, AgentUpdateRequestStatus.Failed, isActive: false,
            startedAt: Now.UtcDateTime.AddDays(-1), id: 4);
        await SaveAsync();

        Assert.Empty(await _repository.FailExpiredAsync(Ct));
    }

    [Fact]
    public async Task FailExpiredAsync_LeavesAnAlreadyTerminalTaskUntouchedWhileStillFailingTheRequest()
    {
        var server = AddServer(1, "runner");
        AddRequest(
            server,
            AgentUpdateRequestStatus.Handoff,
            handoffAt: Now.UtcDateTime.AddMinutes(-10),
            confirmationDeadline: Now.UtcDateTime.AddMinutes(-5),
            task: UpdateTask(status: TaskExecutionStatus.Success));
        await SaveAsync();

        var request = Assert.Single(await _repository.FailExpiredAsync(Ct));

        Assert.Equal(AgentUpdateRequestStatus.Failed, request.Status);
        Assert.Equal(TaskExecutionStatus.Success, request.Task!.Status);
        Assert.Null(request.Task.ExitCode);
    }

    public void Dispose() => _db.Dispose();
}
