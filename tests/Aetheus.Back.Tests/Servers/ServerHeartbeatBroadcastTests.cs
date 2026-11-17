// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>
/// Recette R-2: the export carried heartbeats of 13 to 15.6 s whose whole time sat in the "broadcast" stage, the
/// live update sent to the browsers watching the server. A client whose connection is dead keeps a SignalR send
/// pending until its keep-alive notices, so awaiting the send inside the agent's request held the agent's call
/// (and its request-length "Slow request" line) for that long. The beat is persisted before the broadcast, so the
/// broadcast must not hold the request, and a failed one is told instead of failing a beat that is already saved.
/// </summary>
public sealed class ServerHeartbeatBroadcastTests : IDisposable
{
    private const int ServerId = 7;

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly List<(LogLevel Level, string Message)> _logs = [];
    private readonly TaskCompletionSource _warned = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ServerHeartbeatBroadcastTests()
    {
        _db.Servers.Add(new Server { Id = ServerId, Name = "agent-7", Hostname = "agent-7", OsDescription = "Ubuntu 24.04 LTS" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private Task BeatAsync(Task send)
    {
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>()).Returns(send);
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(proxy);
        clients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(proxy);
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(clients);
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(Substitute.For<IHubClients>());
        var factory = LoggerFactory.Create(builder => builder.AddProvider(new Capture(_logs, _warned)));
        var service = new ServerService(new ServerRepository(_db, _time), new ServerHeartbeatRepository(_db), hub,
            alertHub, Substitute.For<IAuditService>(), Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), _time, Substitute.For<IDbTransactionScope>(), factory);
        return service.ProcessHeartbeatAsync(ServerId, new ServerHeartbeatDto { AgentVersion = "1.0.0" }, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABroadcastThatNeverCompletes_DoesNotHoldTheHeartbeat()
    {
        var neverCompletes = new TaskCompletionSource().Task;

        await BeatAsync(neverCompletes);

        var stored = await _db.Servers.AsNoTracking().SingleAsync(server => server.Id == ServerId, TestContext.Current.CancellationToken);
        Assert.Equal("1.0.0", stored.AgentVersion);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailedBroadcast_IsToldAsAWarning_AndTheSavedBeatSucceeds()
    {
        await BeatAsync(Task.FromException(new InvalidOperationException("hub send refused")));

        await _warned.Task.WaitAsync(TestContext.Current.CancellationToken);
        var warning = Assert.Single(_logs, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("broadcast", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"server {ServerId}", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Capture(List<(LogLevel Level, string Message)> sink, TaskCompletionSource warned) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (sink) sink.Add((logLevel, formatter(state, exception)));
            if (logLevel == LogLevel.Warning) warned.TrySetResult();
        }
        public void Dispose()
        {
        }
    }
}
