// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>An agent says how its previous process ended with its first heartbeat: the backend logs it once,
/// keeps it for the sweep that fails the replaced session's tasks, and says nothing when the agent sent nothing.</summary>
public sealed class ServerHeartbeatPreviousExitTests : IDisposable
{
    private const int ServerId = 7;
    private const string Boot1 = "11111111-1111-1111-1111-111111111111";
    private const string Boot2 = "22222222-2222-2222-2222-222222222222";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 1, 8, 0, TimeSpan.Zero));
    private readonly RecordingLoggerFactory _logs = new();
    private readonly AgentRestartRegistry _registry;

    public ServerHeartbeatPreviousExitTests()
    {
        _registry = new AgentRestartRegistry(_time);
        _db.Servers.Add(new Server { Id = ServerId, Name = "vps-5", Hostname = "vps-5", AgentSessionId = "session-1" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private Task BeatAsync(AgentExitReportDto? previousExit)
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(Substitute.For<IHubClients>());
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(Substitute.For<IHubClients>());
        var service = new ServerService(new ServerRepository(_db, _time), new ServerHeartbeatRepository(_db), hub,
            alertHub, Substitute.For<IAuditService>(), Substitute.For<ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), _time, Substitute.For<IDbTransactionScope>(),
            loggerFactory: _logs, restartRegistry: _registry);
        return service.ProcessHeartbeatAsync(ServerId, new ServerHeartbeatDto
        {
            AgentVersion = "1.0.0",
            AgentSessionId = "session-2",
            PreviousExit = previousExit
        }, TestContext.Current.CancellationToken);
    }

    private static AgentExitReportDto Exit(string kind, string? previousBoot, string? boot) => new()
    {
        Kind = kind,
        Reason = "self-repair: polling loop stalled",
        OccurredAtUtc = new DateTime(2026, 11, 8, 1, 7, 0, DateTimeKind.Utc),
        PreviousBootId = previousBoot,
        BootId = boot
    };

    [Fact]
    public async Task APreviousExit_IsLoggedOnceAsAWarningNamingKindReasonAndNoReboot()
    {
        await BeatAsync(Exit(AgentExitKinds.Restart, Boot1, Boot1));

        var warning = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(
            "Agent of server 7 restarted: Restart self-repair: polling loop stalled (host rebooted: no)",
            warning.Message);
    }

    [Fact]
    public async Task DifferentBootIds_AreLoggedAsAHostReboot()
    {
        await BeatAsync(Exit(AgentExitKinds.Unclean, Boot1, Boot2));

        var warning = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.EndsWith("(host rebooted: yes)", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingBootId_IsLoggedAsUnknownNeverAsNo()
    {
        await BeatAsync(Exit(AgentExitKinds.Stopped, null, null));

        var warning = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.EndsWith("(host rebooted: unknown)", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeartbeatWithoutTheField_StillWorksAndLogsNoRestart()
    {
        await BeatAsync(previousExit: null);

        Assert.DoesNotContain(_logs.Entries, entry => entry.Message.Contains("restarted", StringComparison.Ordinal));
        Assert.Null(_registry.Find(ServerId));
        var server = await _db.Servers.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("session-2", server.AgentSessionId);
    }

    [Fact]
    public async Task TheReportedCause_IsKeptForTheSweepAndExpires()
    {
        await BeatAsync(Exit(AgentExitKinds.Restart, Boot1, Boot1));

        var found = _registry.Find(ServerId);
        Assert.NotNull(found);
        Assert.Equal(AgentExitKinds.Restart, found.Kind);
        Assert.Equal(HostRebootVerdict.No, found.HostRebooted);

        _time.Advance(AgentRestartRegistry.Retention + TimeSpan.FromMinutes(1));
        Assert.Null(_registry.Find(ServerId));
    }

    [Fact]
    public async Task TheSweep_ExplainsTheFailedTaskWithTheReportedCause()
    {
        await BeatAsync(Exit(AgentExitKinds.Restart, Boot1, Boot1));
        var orphaned = Orphaned(ServerId);

        await SweepAsync(orphaned, _registry);

        Assert.Equal(TaskExecutionStatus.Failed, orphaned.Status);
        Assert.StartsWith(PipelineOutageReplayPolicy.NotReplayedReason, orphaned.FailureReason, StringComparison.Ordinal);
        Assert.Contains("Restart", orphaned.FailureReason, StringComparison.Ordinal);
        Assert.Contains("self-repair: polling loop stalled", orphaned.FailureReason, StringComparison.Ordinal);
        Assert.EndsWith("host rebooted: no.", orphaned.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSweep_KeepsTheGenericTextWhenNoCauseIsKnown()
    {
        var orphaned = Orphaned(ServerId);

        await SweepAsync(orphaned, _registry);

        Assert.Equal(PipelineOutageReplayPolicy.NotReplayedReason, orphaned.FailureReason);
    }

    [Fact]
    public async Task TheSweep_ReadsNoCauseOfAnotherServer()
    {
        await BeatAsync(Exit(AgentExitKinds.Restart, Boot1, Boot1));
        var orphaned = Orphaned(ServerId + 1);

        await SweepAsync(orphaned, _registry);

        Assert.Equal(PipelineOutageReplayPolicy.NotReplayedReason, orphaned.FailureReason);
    }

    private ServerTask Orphaned(int serverId) => new()
    {
        Id = 34133,
        ServerId = serverId,
        Name = "Build",
        Status = TaskExecutionStatus.Running,
        StartedAt = _time.GetUtcNow().UtcDateTime.AddMinutes(-2),
        AssignedAgentSessionId = "session-1"
    };

    private async Task SweepAsync(ServerTask task, AgentRestartRegistry registry)
    {
        var repo = Substitute.For<ITaskRepository>();
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetStaleAssignedTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetTasksFromSupersededAgentSessionsAsync(Arg.Any<CancellationToken>()).Returns([task]);
        var scopes = new ServiceCollection().AddScoped(_ => repo).BuildServiceProvider();
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(clients);
        var sweep = new TaskTimeoutService(
            scopes.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<TaskTimeoutService>>(),
            Options.Create(new BackgroundServicesOptions()),
            hub,
            _time,
            restartRegistry: registry,
            leaderLease: LeaderLeaseDouble.Leader());

        await sweep.CheckStaleTasksAsync(TestContext.Current.CancellationToken);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void AddProvider(ILoggerProvider provider)
        {
            // Not used: the factory only hands out its recording logger.
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    private sealed class RecordingLogger(List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
