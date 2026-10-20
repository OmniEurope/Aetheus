// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ServerTimeoutServiceTests
{
    private static (ServerTimeoutService Sut, IServerRepository Repo, IClientProxy Proxy, IDomainEventDispatcher Events)
        BuildSut(params Aetheus.Back.Data.Entities.Server[] stale)
    {
        var serverRepo = Substitute.For<IServerRepository>();
        var hubContext = Substitute.For<IHubContext<ServerHub>>();
        var hubClients = Substitute.For<IHubClients>();
        var clientProxy = Substitute.For<IClientProxy>();
        var events = Substitute.For<IDomainEventDispatcher>();

        hubContext.Clients.Returns(hubClients);
        hubClients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(clientProxy);

        serverRepo.GetStaleOnlineServersAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(stale.ToList());
        serverRepo.TryMarkOfflineIfStaleAsync(
                Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));

        // External probe (PLAN-010 lot 1): the host answers with a refused connect, no network involved.
        var prober = Substitute.For<ITcpReachabilityProber>();
        prober.ConnectAsync(Arg.Any<System.Net.IPAddress>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(TcpProbeOutcome.Refused);
        var reachabilityRepo = Substitute.For<IServerReachabilityRepository>();
        reachabilityRepo.GetOfflineServersDueForProbeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var services = new ServiceCollection();
        services.AddScoped(_ => serverRepo);
        services.AddScoped(_ => hubContext);
        services.AddScoped(_ => events);
        services.AddScoped(_ => new ServerReachabilityProbe(
            prober, reachabilityRepo, Options.Create(new ServerReachabilityProbeOptions()), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerReachabilityProbe>.Instance));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var sut = new ServerTimeoutService(scopeFactory, Substitute.For<ILogger<ServerTimeoutService>>(),
            Options.Create(new BackgroundServicesOptions()), clock);
        return (sut, serverRepo, clientProxy, events);
    }

    [Fact]
    public async Task CheckServerTimeouts_MarksStaleServersOffline_SavesAndBroadcasts()
    {
        var staleServer = new Aetheus.Back.Data.Entities.Server
        {
            Id = 1,
            Name = "stale-server",
            Status = ServerStatus.Online,
            LastHeartbeat = new DateTime(2026, 6, 16, 11, 55, 0, DateTimeKind.Utc)
        };
        var (sut, repo, proxy, events) = BuildSut(staleServer);

        await sut.CheckServerTimeouts(TestContext.Current.CancellationToken);

        await repo.Received(1).TryMarkOfflineIfStaleAsync(
            1, staleServer.LastHeartbeat, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await proxy.Received(1).SendCoreAsync("ServerOffline", Arg.Is<object?[]>(a => (int)a[0]! == 1), Arg.Any<CancellationToken>());
        events.Received(1).Publish(Arg.Is<ServerWentOfflineEvent>(e => e.ServerId == 1));
    }

    [Fact]
    public async Task CheckServerTimeouts_OfflineEventCarriesExternalProbeResult()
    {
        // PLAN-010 lot 1: the alert must say which case it is, so the probe runs before the event.
        var staleServer = new Aetheus.Back.Data.Entities.Server
        {
            Id = 3,
            Name = "vps",
            IpAddress = "203.0.113.5",
            Status = ServerStatus.Online,
            LastHeartbeat = new DateTime(2026, 6, 16, 11, 55, 0, DateTimeKind.Utc)
        };
        var (sut, _, _, events) = BuildSut(staleServer);

        await sut.CheckServerTimeouts(TestContext.Current.CancellationToken);

        events.Received(1).Publish(Arg.Is<ServerWentOfflineEvent>(e =>
            e.ServerId == 3 && e.Reachability == ServerReachability.HostReachable));
    }

    [Fact]
    public async Task CheckServerTimeouts_ServerWithoutAddress_EventSaysUnknown()
    {
        var staleServer = new Aetheus.Back.Data.Entities.Server
        {
            Id = 4,
            Name = "no-address",
            Status = ServerStatus.Online,
            LastHeartbeat = new DateTime(2026, 6, 16, 11, 55, 0, DateTimeKind.Utc)
        };
        var (sut, _, _, events) = BuildSut(staleServer);

        await sut.CheckServerTimeouts(TestContext.Current.CancellationToken);

        events.Received(1).Publish(Arg.Is<ServerWentOfflineEvent>(e =>
            e.ServerId == 4 && e.Reachability == ServerReachability.UnknownNoAddress));
    }

    [Fact]
    public async Task CheckServerTimeouts_NoStaleServers_DoesNotSaveOrBroadcast()
    {
        var (sut, repo, proxy, events) = BuildSut();

        await sut.CheckServerTimeouts(TestContext.Current.CancellationToken);

        await repo.DidNotReceive().TryMarkOfflineIfStaleAsync(
            Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await proxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
        events.DidNotReceive().Publish(Arg.Any<ServerWentOfflineEvent>());
    }

    [Fact]
    public async Task CheckServerTimeouts_ConcurrentHeartbeatWon_DoesNotBroadcastOffline()
    {
        var staleServer = new Aetheus.Back.Data.Entities.Server
        {
            Id = 1,
            Name = "stale-server",
            Status = ServerStatus.Online,
            LastHeartbeat = new DateTime(2026, 6, 16, 11, 55, 0, DateTimeKind.Utc)
        };
        var (sut, repo, proxy, events) = BuildSut(staleServer);
        repo.TryMarkOfflineIfStaleAsync(
                staleServer.Id, staleServer.LastHeartbeat, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await sut.CheckServerTimeouts(TestContext.Current.CancellationToken);

        await proxy.DidNotReceive().SendCoreAsync(
            Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
        events.DidNotReceive().Publish(Arg.Any<ServerWentOfflineEvent>());
    }

    [Fact]
    public async Task StartupGrace_BackendRestart_WaitsFullHeartbeatWindow()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var options = new BackgroundServicesOptions
        {
            ServerHeartbeatTimeout = TimeSpan.FromMinutes(2),
            ServerCheckInterval = TimeSpan.FromMinutes(1)
        };
        var sut = new ServerTimeoutService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<ServerTimeoutService>>(),
            Options.Create(options),
            clock);

        var startupGrace = sut.WaitForStartupGraceAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(59)));
        Assert.False(startupGrace.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await startupGrace;
    }
}
