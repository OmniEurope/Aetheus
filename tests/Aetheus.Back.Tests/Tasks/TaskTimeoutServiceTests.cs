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

public class TaskTimeoutServiceTests
{
    private static (TaskTimeoutService Sut, ITaskRepository Repo, IClientProxy Proxy) BuildSut(FakeTimeProvider clock)
    {
        var repo = Substitute.For<ITaskRepository>();
        // Default empty sweeps; individual tests override the state(s) they exercise.
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetStaleAssignedTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetTasksFromSupersededAgentSessionsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var sp = new ServiceCollection().AddScoped(_ => repo).BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var logger = Substitute.For<ILogger<TaskTimeoutService>>();
        var options = Options.Create(new BackgroundServicesOptions());

        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(proxy);
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(clients);

        return (new TaskTimeoutService(scopeFactory, logger, options, hub, clock), repo, proxy);
    }

    [Fact]
    public async Task CheckStaleTasksAsync_MarksStuckRunningTaskTimedOutAndSaves()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var (sut, repo, _) = BuildSut(clock);

        var staleTask = new ServerTask { Id = 7, Name = "stuck", Status = TaskExecutionStatus.Running };
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([staleTask]);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, staleTask.Status);
        Assert.Equal(new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc), staleTask.CompletedAt);
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckStaleTasksAsync_MarksStuckPendingTaskTimedOutAndBroadcasts()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var (sut, repo, proxy) = BuildSut(clock);

        // The apache2-install regression: a Pending task no agent ever claimed.
        var pending = new ServerTask { Id = 9, ServerId = 3, Name = "Install - apache2", Status = TaskExecutionStatus.Pending };
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([pending]);
        repo.GetServerStatusAsync(3, Arg.Any<CancellationToken>()).Returns(ServerStatus.Offline);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, pending.Status);
        Assert.Equal(new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc), pending.CompletedAt);
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        // Per-server group + all-servers group => two TaskCompleted sends.
        await proxy.Received(2).SendCoreAsync("TaskCompleted", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckStaleTasksAsync_AgentSessionChanged_ReconcilesRunningTaskImmediately()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 25, 18, 0, 0, TimeSpan.Zero));
        var (sut, repo, _) = BuildSut(clock);
        var orphaned = new ServerTask
        {
            Id = 17,
            ServerId = 4,
            Name = "DAST active",
            Status = TaskExecutionStatus.Running,
            StartedAt = clock.GetUtcNow().UtcDateTime.AddMinutes(-2),
            AssignedAgentSessionId = "11111111111111111111111111111111"
        };
        repo.GetTasksFromSupersededAgentSessionsAsync(Arg.Any<CancellationToken>())
            .Returns([orphaned]);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, orphaned.Status);
        Assert.Equal(PipelineOutageReplayPolicy.NotReplayedReason, orphaned.FailureReason);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, orphaned.CompletedAt);
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckStaleTasksAsync_DeferredCleanupLosesLease_RequeuesInsteadOfTimingOut()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 25, 18, 0, 0, TimeSpan.Zero));
        var (sut, repo, proxy) = BuildSut(clock);
        var cleanup = new ServerTask
        {
            Id = 18,
            ServerId = 4,
            Name = "Cleanup",
            Status = TaskExecutionStatus.Assigned,
            AssignedAt = clock.GetUtcNow().UtcDateTime.AddMinutes(-5),
            AssignedAgentSessionId = "old-session",
            IsDeferredCleanup = true
        };
        repo.GetStaleAssignedTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([cleanup]);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Pending, cleanup.Status);
        Assert.Null(cleanup.AssignedAt);
        Assert.Null(cleanup.AssignedAgentSessionId);
        Assert.Null(cleanup.CompletedAt);
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await proxy.DidNotReceive().SendCoreAsync(
            "TaskCompleted",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckStaleTasksAsync_RechecksPollingLeaseAndKeepsOldPendingTaskForActiveRunner()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 24, 10, 15, 0, TimeSpan.Zero));
        var (sut, repo, proxy) = BuildSut(clock);
        var pending = new ServerTask
        {
            Id = 2638,
            ServerId = 11,
            Name = "Scan source with OpenGrep",
            Status = TaskExecutionStatus.Pending,
            CreatedAt = clock.GetUtcNow().UtcDateTime.AddMinutes(-10)
        };
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([pending]);
        repo.IsServerTaskPollingActiveAsync(11, Arg.Any<CancellationToken>()).Returns(true);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Pending, pending.Status);
        Assert.Null(pending.CompletedAt);
        await repo.Received(1).IsServerTaskPollingActiveAsync(11, Arg.Any<CancellationToken>());
        await repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await proxy.DidNotReceive().SendCoreAsync(
            Arg.Any<string>(),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckStaleTasksAsync_HeartbeatingRunnerWithoutTaskPolling_TimesOutQueue()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 24, 10, 15, 0, TimeSpan.Zero));
        var (sut, repo, proxy) = BuildSut(clock);
        var pending = new ServerTask
        {
            Id = 2639,
            ServerId = 12,
            Name = "Blocked behind dead task poller",
            Status = TaskExecutionStatus.Pending,
            CreatedAt = clock.GetUtcNow().UtcDateTime.AddMinutes(-10)
        };
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([pending]);
        repo.GetServerStatusAsync(12, Arg.Any<CancellationToken>()).Returns(ServerStatus.Online);
        repo.IsServerTaskPollingActiveAsync(12, Arg.Any<CancellationToken>()).Returns(false);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, pending.Status);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, pending.CompletedAt);
        await repo.Received(1).IsServerTaskPollingActiveAsync(12, Arg.Any<CancellationToken>());
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await proxy.Received(2).SendCoreAsync(
            "TaskCompleted",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckStaleTasksAsync_UsesShortAssignedStartTimeout()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var (sut, repo, _) = BuildSut(clock);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        await repo.Received(1).GetStaleAssignedTasksAsync(
            TimeSpan.FromMinutes(2), Arg.Any<CancellationToken>());
        await repo.Received(1).GetStaleRunningTasksAsync(
            TimeSpan.FromMinutes(30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckStaleTasksAsync_NoStaleTasks_DoesNotSaveOrBroadcast()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var (sut, repo, proxy) = BuildSut(clock);

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        await repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await proxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }
}
