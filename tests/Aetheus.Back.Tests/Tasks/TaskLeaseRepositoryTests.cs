// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Tasks;

/// <summary>
/// The agent-session lease and the fencing token it hands out: who may claim a server, when the
/// token is bumped, and which task transitions a stale token is refused. Each public entry point
/// has a relational implementation (row locks, ExecuteUpdate inside a transaction) and a store
/// implementation; the tests below drive the store one, which encodes the same decisions.
/// </summary>
public sealed class TaskLeaseRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTime NowUtc = Now.UtcDateTime;

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly AppDbContext _db;
    private readonly TaskLeaseRepository _repository;

    public TaskLeaseRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AppDbContext(options, _clock);
        _repository = new TaskLeaseRepository(_db, _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SaveAsync() => await _db.SaveChangesAsync(Ct);

    private Server AddServer(
        int id = 1, string? sessionId = null, DateTime? leaseExpiresAt = null, long fencingToken = 0)
    {
        var server = new Server
        {
            Id = id,
            Name = $"server-{id}",
            AgentSessionId = sessionId,
            AgentSessionLeaseExpiresAt = leaseExpiresAt,
            AgentSessionFencingToken = fencingToken
        };
        _db.Servers.Add(server);
        return server;
    }

    private ServerTask AddTask(
        int id = 100,
        int serverId = 1,
        TaskExecutionStatus status = TaskExecutionStatus.Assigned,
        string? sessionId = "session-a",
        long fencingToken = 1,
        int? stepRunId = null)
    {
        var task = new ServerTask
        {
            Id = id,
            ServerId = serverId,
            Name = "work",
            Status = status,
            AssignedAgentSessionId = sessionId,
            AssignedAgentSessionFencingToken = fencingToken,
            PipelineStepRunId = stepRunId
        };
        _db.Tasks.Add(task);
        return task;
    }

    private PipelineStepRun AddStep(int id, TaskExecutionStatus status = TaskExecutionStatus.Pending)
    {
        var step = new PipelineStepRun
        {
            Id = id,
            StageName = "build",
            StepName = "compile",
            Status = status
        };
        _db.PipelineStepRuns.Add(step);
        return step;
    }

    // ---------- acquiring the lease ----------

    [Fact]
    public async Task AcquireAgentSessionLeaseAsync_ClaimsAFreeServerAndIssuesTheFirstToken()
    {
        var server = AddServer();
        await SaveAsync();

        var token = await _repository.AcquireAgentSessionLeaseAsync(1, "session-a", Ct);

        Assert.Equal(1, token);
        Assert.Equal("session-a", server.AgentSessionId);
        Assert.Equal(NowUtc.AddMinutes(2), server.AgentSessionLeaseExpiresAt);
    }

    [Fact]
    public async Task AcquireAgentSessionLeaseAsync_RenewsTheSameSessionWithoutBumpingTheToken()
    {
        var server = AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 4);
        await SaveAsync();

        var token = await _repository.AcquireAgentSessionLeaseAsync(1, "session-a", Ct);

        Assert.Equal(4, token);
        Assert.Equal(NowUtc.AddMinutes(2), server.AgentSessionLeaseExpiresAt);
    }

    /// <summary>A renewed lease for the same process session must preserve its fencing token so the
    /// agent retains ownership of running work.</summary>
    [Fact]
    public async Task AcquireAgentSessionLeaseAsync_KeepsTheTokenWhenTheSameSessionCameBackAfterExpiry()
    {
        var server = AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(-1), fencingToken: 4);
        await SaveAsync();

        var token = await _repository.AcquireAgentSessionLeaseAsync(1, "session-a", Ct);

        Assert.Equal(4, token);
        Assert.Equal(4, server.AgentSessionFencingToken);
        Assert.Equal("session-a", server.AgentSessionId);
        Assert.Equal(NowUtc.AddMinutes(2), server.AgentSessionLeaseExpiresAt);
    }

    [Fact]
    public async Task AcquireAgentSessionLeaseAsync_RefusesAnotherSessionWhileTheLeaseIsAlive()
    {
        var server = AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 4);
        await SaveAsync();

        Assert.Null(await _repository.AcquireAgentSessionLeaseAsync(1, "session-b", Ct));
        Assert.Equal("session-a", server.AgentSessionId);
        Assert.Equal(4, server.AgentSessionFencingToken);
    }

    [Fact]
    public async Task AcquireAgentSessionLeaseAsync_HandsTheServerToANewSessionOnceTheLeaseExpired()
    {
        var server = AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(-1), fencingToken: 4);
        await SaveAsync();

        var token = await _repository.AcquireAgentSessionLeaseAsync(1, "session-b", Ct);

        Assert.Equal(5, token);
        Assert.Equal("session-b", server.AgentSessionId);
    }

    [Fact]
    public async Task AcquireAgentSessionLeaseAsync_ReturnsNullForAnUnknownServer()
    {
        Assert.Null(await _repository.AcquireAgentSessionLeaseAsync(404, "session-a", Ct));
    }

    // ---------- checking the lease ----------

    [Fact]
    public async Task HasCurrentAgentLeaseAsync_AcceptsATaskFencedByTheLiveLease()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        AddTask();
        await SaveAsync();

        Assert.True(await _repository.HasCurrentAgentLeaseAsync(100, 1, "session-a", 1, Ct));
    }

    [Fact]
    public async Task HasCurrentAgentLeaseAsync_RejectsAStaleTokenAWrongSessionAndAnExpiredLease()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 2);
        AddTask(fencingToken: 2);
        AddServer(id: 2, sessionId: "session-b", leaseExpiresAt: NowUtc.AddMinutes(-1), fencingToken: 3);
        AddTask(id: 200, serverId: 2, sessionId: "session-b", fencingToken: 3);
        await SaveAsync();

        Assert.False(await _repository.HasCurrentAgentLeaseAsync(100, 1, "session-a", 1, Ct));
        Assert.False(await _repository.HasCurrentAgentLeaseAsync(100, 1, "session-z", 2, Ct));
        Assert.False(await _repository.HasCurrentAgentLeaseAsync(100, 2, "session-a", 2, Ct));
        Assert.False(await _repository.HasCurrentAgentLeaseAsync(200, 2, "session-b", 3, Ct));
    }

    [Fact]
    public async Task HaveCurrentAgentLeasesAsync_RefusesEmptyBatchesAndNonPositiveTokens()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        AddTask();
        await SaveAsync();

        Assert.False(await _repository.HaveCurrentAgentLeasesAsync([], 1, "session-a", 1, Ct));
        Assert.False(await _repository.HaveCurrentAgentLeasesAsync([100], 1, "session-a", 0, Ct));
        Assert.False(await _repository.HaveCurrentAgentLeasesAsync([100], 1, "session-a", -1, Ct));
    }

    [Fact]
    public async Task HaveCurrentAgentLeasesAsync_RequiresEveryRequestedTaskToMatch()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        AddTask(id: 100);
        AddTask(id: 101);
        AddTask(id: 102, fencingToken: 0);
        await SaveAsync();

        Assert.True(await _repository.HaveCurrentAgentLeasesAsync([100, 101], 1, "session-a", 1, Ct));
        Assert.False(await _repository.HaveCurrentAgentLeasesAsync([100, 102], 1, "session-a", 1, Ct));
        Assert.False(await _repository.HaveCurrentAgentLeasesAsync([100, 404], 1, "session-a", 1, Ct));
    }

    // ---------- starting a task ----------

    [Fact]
    public async Task TryStartTaskWithLeaseAsync_StartsAnAssignedTaskHeldByTheLiveLease()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        var task = AddTask();
        await SaveAsync();

        var started = await _repository.TryStartTaskWithLeaseAsync(task, "session-a", 1, NowUtc, Ct);

        Assert.True(started);
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
        Assert.Equal(NowUtc, task.StartedAt);
    }

    [Fact]
    public async Task TryStartTaskWithLeaseAsync_RefusesAStaleTokenOrANonAssignedTask()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 2);
        var stale = AddTask(id: 100, fencingToken: 1);
        var running = AddTask(id: 101, status: TaskExecutionStatus.Running, fencingToken: 2);
        await SaveAsync();

        Assert.False(await _repository.TryStartTaskWithLeaseAsync(stale, "session-a", 2, NowUtc, Ct));
        Assert.False(await _repository.TryStartTaskWithLeaseAsync(running, "session-a", 2, NowUtc, Ct));
        Assert.Equal(TaskExecutionStatus.Assigned, stale.Status);
    }

    [Fact]
    public async Task TryStartTaskWithLeaseAsync_StartsTheOwningPipelineStepAlongWithTheTask()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        var task = AddTask(stepRunId: 500);
        var step = AddStep(500);
        await SaveAsync();

        Assert.True(await _repository.TryStartTaskWithLeaseAsync(task, "session-a", 1, NowUtc, Ct));
        Assert.Equal(TaskExecutionStatus.Running, step.Status);
        Assert.Equal(NowUtc, step.StartedAt);
    }

    [Fact]
    public async Task TryStartTaskWithLeaseAsync_RefusesToStartWhenTheStepIsNoLongerStartable()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        var task = AddTask(stepRunId: 500);
        AddStep(500, TaskExecutionStatus.Cancelled);
        await SaveAsync();

        Assert.False(await _repository.TryStartTaskWithLeaseAsync(task, "session-a", 1, NowUtc, Ct));
        Assert.Equal(TaskExecutionStatus.Assigned, task.Status);
    }

    [Fact]
    public async Task TryStartTaskWithLeaseAsync_RefusesToStartWhenTheStepRowIsGone()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        var task = AddTask(stepRunId: 500);
        await SaveAsync();

        Assert.False(await _repository.TryStartTaskWithLeaseAsync(task, "session-a", 1, NowUtc, Ct));
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Pending)]
    [InlineData(TaskExecutionStatus.Assigned)]
    public async Task TryStartTaskAsync_StartsAnUnstartedTaskWithoutRequiringALease(
        TaskExecutionStatus initial)
    {
        var task = AddTask(status: initial);
        await SaveAsync();

        Assert.True(await _repository.TryStartTaskAsync(task, NowUtc, Ct));
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
        Assert.Equal(NowUtc, task.StartedAt);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.Success)]
    [InlineData(TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Cancelled)]
    public async Task TryStartTaskAsync_RefusesATaskThatAlreadyLeftTheStartingStates(
        TaskExecutionStatus initial)
    {
        var task = AddTask(status: initial);
        await SaveAsync();

        Assert.False(await _repository.TryStartTaskAsync(task, NowUtc, Ct));
        Assert.Equal(initial, task.Status);
    }

    [Fact]
    public async Task TryStartTaskAsync_AlsoStartsTheOwningStepAndRefusesAnUnstartableOne()
    {
        var startable = AddTask(id: 100, status: TaskExecutionStatus.Pending, stepRunId: 500);
        var blocked = AddTask(id: 101, status: TaskExecutionStatus.Pending, stepRunId: 501);
        var step = AddStep(500);
        AddStep(501, TaskExecutionStatus.Success);
        await SaveAsync();

        Assert.True(await _repository.TryStartTaskAsync(startable, NowUtc, Ct));
        Assert.Equal(TaskExecutionStatus.Running, step.Status);
        Assert.False(await _repository.TryStartTaskAsync(blocked, NowUtc, Ct));
        Assert.Equal(TaskExecutionStatus.Pending, blocked.Status);
    }

    // ---------- completing a task ----------

    [Fact]
    public async Task TryCompleteTaskWithLeaseAsync_WritesTheOutcomeWhenTheLeaseIsStillCurrent()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        var task = AddTask(status: TaskExecutionStatus.Running);
        await SaveAsync();

        var completed = await _repository.TryCompleteTaskWithLeaseAsync(
            task, "session-a", 1, TaskExecutionStatus.Success, 0, NowUtc, "{}", Ct);

        Assert.True(completed);
        Assert.Equal(TaskExecutionStatus.Success, task.Status);
        Assert.Equal(0, task.ExitCode);
        Assert.Equal(NowUtc, task.CompletedAt);
        Assert.Equal("{}", task.EnvironmentVariables);
    }

    [Fact]
    public async Task TryCompleteTaskWithLeaseAsync_RefusesAnAgentWhoseLeaseWasTakenOver()
    {
        AddServer(sessionId: "session-b", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 2);
        var task = AddTask(status: TaskExecutionStatus.Running);
        await SaveAsync();

        var completed = await _repository.TryCompleteTaskWithLeaseAsync(
            task, "session-a", 1, TaskExecutionStatus.Success, 0, NowUtc, "{}", Ct);

        Assert.False(completed);
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
        Assert.Null(task.CompletedAt);
    }

    // ---------- handing a never-started task back to the queue ----------

    [Fact]
    public async Task TryReleaseAssignedTaskWithLeaseAsync_ReturnsAnAssignedTaskToThePendingQueueAndClearsItsAssignment()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        var task = AddTask();
        task.AssignedAt = NowUtc.AddSeconds(-5);
        await SaveAsync();

        var released = await _repository.TryReleaseAssignedTaskWithLeaseAsync(task, "session-a", 1, Ct);

        Assert.True(released);
        Assert.Equal(TaskExecutionStatus.Pending, task.Status);
        Assert.Null(task.AssignedAt);
        Assert.Null(task.AssignedAgentSessionId);
        Assert.Null(task.AssignedAgentSessionFencingToken);
    }

    [Fact]
    public async Task TryReleaseAssignedTaskWithLeaseAsync_RefusesATaskThatAlreadyStartedExecuting()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        var task = AddTask();
        task.StartedAt = NowUtc.AddSeconds(-1);
        await SaveAsync();

        Assert.False(await _repository.TryReleaseAssignedTaskWithLeaseAsync(task, "session-a", 1, Ct));
        Assert.Equal(TaskExecutionStatus.Assigned, task.Status);
        Assert.Equal("session-a", task.AssignedAgentSessionId);
    }

    [Fact]
    public async Task TryReleaseAssignedTaskWithLeaseAsync_RefusesATaskThatIsNotAssigned()
    {
        AddServer(sessionId: "session-a", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 1);
        var task = AddTask(status: TaskExecutionStatus.Running);
        await SaveAsync();

        Assert.False(await _repository.TryReleaseAssignedTaskWithLeaseAsync(task, "session-a", 1, Ct));
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
    }

    [Fact]
    public async Task TryReleaseAssignedTaskWithLeaseAsync_RefusesAnAgentWhoseLeaseWasTakenOver()
    {
        AddServer(sessionId: "session-b", leaseExpiresAt: NowUtc.AddMinutes(1), fencingToken: 2);
        var task = AddTask();
        await SaveAsync();

        Assert.False(await _repository.TryReleaseAssignedTaskWithLeaseAsync(task, "session-a", 1, Ct));
        Assert.Equal(TaskExecutionStatus.Assigned, task.Status);
        Assert.Equal("session-a", task.AssignedAgentSessionId);
    }

    public void Dispose() => _db.Dispose();
}
