// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

public class TaskRepositoryStatusTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly TaskRepository _repo;

    public TaskRepositoryStatusTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new TaskRepository(_db, TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    private void SeedTask(int id, int serverId, string name, TaskExecutionStatus status)
    {
        _db.Servers.Add(new Server { Id = serverId, Name = $"srv{serverId}", Hostname = $"s{serverId}", Status = ServerStatus.Online });
        _db.Tasks.Add(new ServerTask { Id = id, ServerId = serverId, Name = name, Command = "echo", Status = status, CreatedAt = new DateTime(2026, 6, id, 0, 0, 0, DateTimeKind.Utc) });
        _db.SaveChanges();
    }

    [Fact]
    public async Task DeleteCompletedTasksOlderThan_UsesTaskAndParentRunRetentionWindows()
    {
        var server = new Server
        {
            Id = 91,
            Name = "retention-runner",
            Hostname = "retention-runner",
            Status = ServerStatus.Offline
        };
        var pipeline = new Pipeline { Id = 91, Name = "retention" };
        var expiredRun = new PipelineRun
        {
            Id = 91,
            Pipeline = pipeline,
            StartedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var retainedRun = new PipelineRun
        {
            Id = 92,
            Pipeline = pipeline,
            StartedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _db.AddRange(server, pipeline, expiredRun, retainedRun);
        _db.Tasks.AddRange(
            NewCompletedTask(91, server, expiredRun),
            NewCompletedTask(92, server, retainedRun),
            NewCompletedTask(93, server, pipelineRun: null));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await _repo.DeleteCompletedTasksOlderThanAsync(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, deleted);
        Assert.Equal([92], await _db.Tasks.Select(task => task.Id).ToListAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        static ServerTask NewCompletedTask(int id, Server server, PipelineRun? pipelineRun) => new()
        {
            Id = id,
            Server = server,
            PipelineRun = pipelineRun,
            Name = $"task-{id}",
            Command = "echo",
            Status = TaskExecutionStatus.Success,
            CreatedAt = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    [Fact]
    public async Task GetTasksByStatusesPagedAsync_FiltersByStatus()
    {
        SeedTask(1, 1, "build", TaskExecutionStatus.Running);
        SeedTask(2, 2, "deploy", TaskExecutionStatus.Success);
        SeedTask(3, 3, "test", TaskExecutionStatus.Failed);

        var (items, total) = await _repo.GetTasksByStatusesPagedAsync(
            null, 1, 10, [TaskExecutionStatus.Running, TaskExecutionStatus.Failed], ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, total);
        Assert.DoesNotContain(items, t => t.Name == "deploy");
    }

    [Fact]
    public async Task GetTasksByStatusesPagedAsync_FiltersByAccessibleServersAndSearch()
    {
        SeedTask(1, 1, "alpha-build", TaskExecutionStatus.Running);
        SeedTask(2, 2, "beta-build", TaskExecutionStatus.Running);

        var (accessible, _) = await _repo.GetTasksByStatusesPagedAsync(
            null, 1, 10, [TaskExecutionStatus.Running], accessibleServerIds: [1], ct: TestContext.Current.CancellationToken);
        Assert.Single(accessible);

        var (searched, _) = await _repo.GetTasksByStatusesPagedAsync(
            "beta", 1, 10, [TaskExecutionStatus.Running], ct: TestContext.Current.CancellationToken);
        Assert.Single(searched);
        Assert.Equal("beta-build", searched[0].Name);
    }

    [Fact]
    public async Task ClaimPendingTasksAsync_SelfUpdateIsExclusiveAndPrioritized()
    {
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "srv1",
            Hostname = "s1",
            Status = ServerStatus.Online,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"shell.execute\"]",
            AgentVersion = "1.2.3",
            ScannerCapabilitiesJson = "[\"scanner-manifest:sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]"
        });
        _db.Tasks.AddRange(
            new ServerTask
            {
                Id = 1,
                ServerId = 1,
                Name = "older normal work",
                Command = "echo",
                Status = TaskExecutionStatus.Pending,
                CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new ServerTask
            {
                Id = 2,
                ServerId = 1,
                Name = "agent update",
                Command = string.Empty,
                Operation = OperationKind.AgentSelfUpdate,
                Status = TaskExecutionStatus.Pending,
                CreatedAt = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc)
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        const string sessionId = "11111111111111111111111111111111";
        var claimed = await _repo.ClaimPendingTasksAsync(
            1,
            take: 2,
            agentSessionId: sessionId,
            ct: TestContext.Current.CancellationToken);

        var update = Assert.Single(claimed);
        Assert.Equal(OperationKind.AgentSelfUpdate, update.Operation);
        Assert.Equal(TaskExecutionStatus.Assigned, update.Status);
        Assert.NotNull(update.AssignedAt);
        Assert.Equal(sessionId, update.AssignedAgentSessionId);
        Assert.Equal(1, update.AssignedAgentSessionFencingToken);
        Assert.Equal("1.2.3", update.AssignedAgentVersion);
        Assert.Equal(new string('a', 64), update.AssignedScannerManifestSha256);
        Assert.Equal(sessionId, (await _db.Servers.FindAsync([1], TestContext.Current.CancellationToken))!.AgentSessionId);
        Assert.Equal(TaskExecutionStatus.Pending, (await _db.Tasks.FindAsync([1], TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task ClaimPendingTasksAsync_AiRunIsClaimedByAgentPublishingAiCapability()
    {
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "ai-runner",
            Hostname = "ai-runner",
            Status = ServerStatus.Online,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = $"[\"{AgentCapabilities.AiExecution}\"]"
        });
        _db.Tasks.Add(new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "AI review",
            Command = "{}",
            Operation = OperationKind.AiRun,
            Status = TaskExecutionStatus.Pending,
            CreatedAt = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc)
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var claimed = await _repo.ClaimPendingTasksAsync(
            1,
            take: 1,
            agentSessionId: "11111111111111111111111111111111",
            ct: TestContext.Current.CancellationToken);

        var task = Assert.Single(claimed);
        Assert.Equal(OperationKind.AiRun, task.Operation);
        Assert.Equal(TaskExecutionStatus.Assigned, task.Status);
    }

    [Fact]
    public async Task ClaimPendingTasksAsync_PreviousProtocolCannotClaimNormalWork()
    {
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "previous-protocol",
            Hostname = "previous-protocol",
            Status = ServerStatus.Online,
            AgentProtocolVersion = AgentProtocol.CurrentVersion - 1,
            AgentCapabilitiesJson = $"[\"{AgentCapabilities.ShellExecution}\"]"
        });
        _db.Tasks.Add(new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "shell task",
            Command = "echo",
            Operation = OperationKind.None,
            Status = TaskExecutionStatus.Pending,
            CreatedAt = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc)
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var claimed = await _repo.ClaimPendingTasksAsync(
            1,
            take: 1,
            agentSessionId: "11111111111111111111111111111111",
            ct: TestContext.Current.CancellationToken);

        Assert.Empty(claimed);
        Assert.Equal(
            TaskExecutionStatus.Pending,
            (await _db.Tasks.FindAsync([1], TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task GetTasksFromSupersededAgentSessionsAsync_ReturnsOnlyOldSessionWork()
    {
        const string oldSession = "11111111111111111111111111111111";
        const string currentSession = "22222222222222222222222222222222";
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "srv1",
            Hostname = "s1",
            Status = ServerStatus.Online,
            AgentSessionId = currentSession,
            AgentSessionFencingToken = 2,
            AgentSessionLeaseExpiresAt = DateTime.MaxValue
        });
        _db.Tasks.AddRange(
            new ServerTask
            {
                Id = 1,
                ServerId = 1,
                Name = "orphaned",
                Command = "scan",
                Status = TaskExecutionStatus.Running,
                AssignedAgentSessionId = oldSession,
                AssignedAgentSessionFencingToken = 1,
                CreatedAt = DateTime.UtcNow
            },
            new ServerTask
            {
                Id = 2,
                ServerId = 1,
                Name = "current",
                Command = "scan",
                Status = TaskExecutionStatus.Running,
                AssignedAgentSessionId = currentSession,
                AssignedAgentSessionFencingToken = 2,
                CreatedAt = DateTime.UtcNow
            },
            new ServerTask
            {
                Id = 3,
                ServerId = 1,
                Name = "agent self-update handoff",
                Command = string.Empty,
                Operation = OperationKind.AgentSelfUpdate,
                Status = TaskExecutionStatus.Running,
                AssignedAgentSessionId = oldSession,
                AssignedAgentSessionFencingToken = 1,
                CreatedAt = DateTime.UtcNow
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var orphaned = await _repo.GetTasksFromSupersededAgentSessionsAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(orphaned).Id);
    }

    /// <summary>
    /// The incident this sweep must not cause: two pipelines shared one runner, a twelve-minute local
    /// build starved the poll loop past the two-minute lease, and the still-running task was parked as
    /// "orphaned by an agent restart" while its own session id was still the one on the server. The
    /// agent then had its log batches refused and its completion answered 409, so a step that had
    /// finished its work was recorded Failed. An agent that lapses and never returns is still settled,
    /// by the Running/Assigned timeouts and the offline parking - not by this sweep.
    /// </summary>
    [Fact]
    public async Task GetTasksFromSupersededAgentSessionsAsync_IgnoresALapsedLeaseHeldByTheSameSession()
    {
        const string session = "33333333333333333333333333333333";
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "srv1",
            Hostname = "s1",
            Status = ServerStatus.Online,
            AgentSessionId = session,
            AgentSessionFencingToken = 7,
            AgentSessionLeaseExpiresAt = DateTime.UtcNow.AddMinutes(-3)
        });
        _db.Tasks.Add(new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "Package the exact validated application",
            Command = "build",
            Status = TaskExecutionStatus.Running,
            AssignedAgentSessionId = session,
            AssignedAgentSessionFencingToken = 7,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var orphaned = await _repo.GetTasksFromSupersededAgentSessionsAsync(
            TestContext.Current.CancellationToken);

        Assert.Empty(orphaned);
    }

    [Fact]
    public async Task ClaimPendingTasksAsync_ExclusiveLeaseFencesSupersededSession()
    {
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var repository = new TaskRepository(_db, time);
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "srv1",
            Hostname = "s1",
            Status = ServerStatus.Online,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"shell.execute\"]"
        });
        _db.Tasks.AddRange(
            new ServerTask
            {
                Id = 1,
                ServerId = 1,
                Name = "first",
                Command = "echo",
                Status = TaskExecutionStatus.Pending
            },
            new ServerTask
            {
                Id = 2,
                ServerId = 1,
                Name = "second",
                Command = "echo",
                Status = TaskExecutionStatus.Pending
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        const string sessionA = "11111111111111111111111111111111";
        const string sessionB = "22222222222222222222222222222222";
        var claimedA = await repository.ClaimPendingTasksAsync(
            1, 1, sessionA, TestContext.Current.CancellationToken);
        var taskA = Assert.Single(claimedA);
        Assert.Equal(1, taskA.AssignedAgentSessionFencingToken);

        var blockedB = await repository.ClaimPendingTasksAsync(
            1, 1, sessionB, TestContext.Current.CancellationToken);
        Assert.Empty(blockedB);

        time.Advance(TimeSpan.FromMinutes(3));
        var claimedB = await repository.ClaimPendingTasksAsync(
            1, 1, sessionB, TestContext.Current.CancellationToken);
        var taskB = Assert.Single(claimedB);
        Assert.Equal(2, taskB.AssignedAgentSessionFencingToken);
        Assert.False(await repository.HasCurrentAgentLeaseAsync(
            taskA.Id, 1, sessionA, 1, TestContext.Current.CancellationToken));
        Assert.True(await repository.HasCurrentAgentLeaseAsync(
            taskB.Id, 1, sessionB, 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClaimPendingTasksAsync_DoesNotRedispatchAssignedTask()
    {
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "srv1",
            Hostname = "s1",
            Status = ServerStatus.Online
        });
        _db.Tasks.Add(new ServerTask
        {
            Id = 1,
            ServerId = 1,
            Name = "already claimed",
            Command = "deploy",
            Status = TaskExecutionStatus.Assigned,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            AssignedAt = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var claimed = await _repo.ClaimPendingTasksAsync(1, take: 1, ct: TestContext.Current.CancellationToken);

        Assert.Empty(claimed);
        Assert.Equal(TaskExecutionStatus.Assigned, (await _db.Tasks.FindAsync([1], TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task GetStaleAssignedTasksAsync_UsesAssignmentTimeInsteadOfCreationTime()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var repository = new TaskRepository(_db, new FakeTimeProvider(now));
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "srv1",
            Hostname = "s1",
            Status = ServerStatus.Online
        });
        _db.Tasks.AddRange(
            new ServerTask
            {
                Id = 1,
                ServerId = 1,
                Name = "fresh claim",
                Command = "build",
                Status = TaskExecutionStatus.Assigned,
                CreatedAt = now.UtcDateTime.AddHours(-1),
                AssignedAt = now.UtcDateTime.AddSeconds(-30)
            },
            new ServerTask
            {
                Id = 2,
                ServerId = 1,
                Name = "stale claim",
                Command = "build",
                Status = TaskExecutionStatus.Assigned,
                CreatedAt = now.UtcDateTime.AddHours(-1),
                AssignedAt = now.UtcDateTime.AddMinutes(-3)
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var stale = await repository.GetStaleAssignedTasksAsync(TimeSpan.FromMinutes(2), ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(stale).Id);
    }

    [Fact]
    public async Task GetStaleRunningTasksAsync_UsesDeclaredTimeoutAndRespectsLongerTaskTimeout()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var repository = new TaskRepository(_db, new FakeTimeProvider(now));
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "srv1",
            Hostname = "s1",
            Status = ServerStatus.Online
        });
        _db.Tasks.AddRange(
            new ServerTask
            {
                Id = 1,
                ServerId = 1,
                Name = "stale default task",
                Command = "build",
                Status = TaskExecutionStatus.Running,
                StartedAt = now.UtcDateTime.AddMinutes(-31),
                TimeoutSeconds = 300
            },
            new ServerTask
            {
                Id = 2,
                ServerId = 1,
                Name = "valid long task",
                Command = "test",
                Status = TaskExecutionStatus.Running,
                StartedAt = now.UtcDateTime.AddMinutes(-31),
                TimeoutSeconds = 2100
            },
            new ServerTask
            {
                Id = 3,
                ServerId = 1,
                Name = "stale long task",
                Command = "test",
                Status = TaskExecutionStatus.Running,
                StartedAt = now.UtcDateTime.AddMinutes(-36),
                TimeoutSeconds = 2100
            },
            new ServerTask
            {
                Id = 4,
                ServerId = 1,
                Name = "short task past its own budget",
                Command = "scan",
                Status = TaskExecutionStatus.Running,
                StartedAt = now.UtcDateTime.AddMinutes(-6),
                TimeoutSeconds = 300
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var stale = await repository.GetStaleRunningTasksAsync(TimeSpan.FromMinutes(30), ct: TestContext.Current.CancellationToken);

        Assert.Equal([1, 3, 4], stale.Select(task => task.Id).Order());
    }

    [Fact]
    public async Task GetStaleRunningTasksAsync_BoundsEachSweepAndKeepsLegacyFallback()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var repository = new TaskRepository(_db, new FakeTimeProvider(now));
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "srv1",
            Hostname = "s1",
            Status = ServerStatus.Online
        });
        _db.Tasks.AddRange(Enumerable.Range(1, 1005).Select(id => new ServerTask
        {
            Id = id,
            ServerId = 1,
            Name = $"stale-{id}",
            Command = "scan",
            Status = TaskExecutionStatus.Running,
            StartedAt = now.UtcDateTime.AddHours(-2).AddSeconds(id),
            TimeoutSeconds = 300
        }));
        _db.Tasks.AddRange(
            new ServerTask
            {
                Id = 2001,
                ServerId = 1,
                Name = "legacy-stale",
                Command = "legacy",
                Status = TaskExecutionStatus.Running,
                StartedAt = now.UtcDateTime.AddMinutes(-31),
                TimeoutSeconds = 0
            },
            new ServerTask
            {
                Id = 2002,
                ServerId = 1,
                Name = "legacy-fresh",
                Command = "legacy",
                Status = TaskExecutionStatus.Running,
                StartedAt = now.UtcDateTime.AddMinutes(-29),
                TimeoutSeconds = 0
            });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var firstSweep = await repository.GetStaleRunningTasksAsync(
            TimeSpan.FromMinutes(30), TestContext.Current.CancellationToken);

        Assert.Equal(1000, firstSweep.Count);
        Assert.DoesNotContain(firstSweep, task => task.Id == 2002);
        Assert.Equal(firstSweep.OrderBy(task => task.StartedAt).ThenBy(task => task.Id), firstSweep);
    }

    [Fact]
    public async Task GetStalePendingTasksAsync_ExpiresQueuesWhoseRunnerStoppedPolling()
    {
        var now = new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);
        var repository = new TaskRepository(_db, new FakeTimeProvider(now));
        _db.Servers.AddRange(
            new Server
            {
                Id = 1,
                Name = "busy-online",
                Hostname = "online",
                Status = ServerStatus.Online,
                AgentSessionLeaseExpiresAt = now.UtcDateTime.AddMinutes(1)
            },
            new Server
            {
                Id = 2,
                Name = "offline",
                Hostname = "offline",
                Status = ServerStatus.Offline
            },
            new Server
            {
                Id = 3,
                Name = "heartbeat-only",
                Hostname = "heartbeat-only",
                Status = ServerStatus.Online,
                LastHeartbeat = now.UtcDateTime,
                AgentSessionLeaseExpiresAt = now.UtcDateTime.AddMinutes(-1)
            });
        _db.Tasks.AddRange(
            new ServerTask
            {
                Id = 1,
                ServerId = 1,
                Name = "queued behind long tasks",
                Command = "scan",
                Status = TaskExecutionStatus.Pending,
                CreatedAt = now.UtcDateTime.AddMinutes(-30)
            },
            new ServerTask
            {
                Id = 2,
                ServerId = 2,
                Name = "orphaned queue entry",
                Command = "scan",
                Status = TaskExecutionStatus.Pending,
                CreatedAt = now.UtcDateTime.AddMinutes(-30)
            },
            new ServerTask
            {
                Id = 3,
                ServerId = 2,
                Name = "durable deferred cleanup",
                Command = "cleanup",
                Status = TaskExecutionStatus.Pending,
                IsDeferredCleanup = true,
                CreatedAt = now.UtcDateTime.AddMinutes(-30)
            },
            new ServerTask
            {
                Id = 4,
                ServerId = 3,
                Name = "queue on heartbeat-only runner",
                Command = "scan",
                Status = TaskExecutionStatus.Pending,
                CreatedAt = now.UtcDateTime.AddMinutes(-30)
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var task in _db.Tasks)
            task.CreatedAt = now.UtcDateTime.AddMinutes(-30);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var stale = await repository.GetStalePendingTasksAsync(
            TimeSpan.FromMinutes(5),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal([2, 4], stale.Select(task => task.Id).Order());
    }

    // Recette R1-42: the agent exits at the end of its self-update, so nothing else is handed to it
    // while that update is claimed or running; the work stays Pending for the new agent.
    [Theory]
    [InlineData(TaskExecutionStatus.Assigned)]
    [InlineData(TaskExecutionStatus.Running)]
    public async Task ClaimPendingTasksAsync_WhileASelfUpdateIsInFlight_ClaimsNothing(TaskExecutionStatus updateStatus)
    {
        SeedServerWithPendingWorkAndUpdate(updateStatus);

        var claimed = await _repo.ClaimPendingTasksAsync(
            1,
            take: 2,
            agentSessionId: "11111111111111111111111111111111",
            ct: TestContext.Current.CancellationToken);

        Assert.Empty(claimed);
        Assert.Equal(
            TaskExecutionStatus.Pending,
            (await _db.Tasks.FindAsync([1], TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task ClaimPendingTasksAsync_OnceTheSelfUpdateIsOver_ClaimsThePendingWork()
    {
        SeedServerWithPendingWorkAndUpdate(TaskExecutionStatus.Success);

        var claimed = await _repo.ClaimPendingTasksAsync(
            1,
            take: 2,
            agentSessionId: "11111111111111111111111111111111",
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(claimed).Id);
    }

    private void SeedServerWithPendingWorkAndUpdate(TaskExecutionStatus updateStatus)
    {
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "srv1",
            Hostname = "s1",
            Status = ServerStatus.Online,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = $"[\"{AgentCapabilities.ShellExecution}\"]"
        });
        _db.Tasks.AddRange(
            new ServerTask
            {
                Id = 1,
                ServerId = 1,
                Name = "normal work",
                Command = "echo",
                Status = TaskExecutionStatus.Pending,
                CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new ServerTask
            {
                Id = 2,
                ServerId = 1,
                Name = "agent update",
                Command = string.Empty,
                Operation = OperationKind.AgentSelfUpdate,
                Status = updateStatus,
                CreatedAt = new DateTime(2026, 10, 1, 0, 1, 0, DateTimeKind.Utc)
            });
        _db.SaveChanges();
    }
}
