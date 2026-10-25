// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// The server hub the loader opens once a server is loaded, on an in-memory hub: it joins that
/// server's group and becomes the loader's connection, moving to another server leaves the first
/// group and replaces the connection, and a reconnection joins the group again.
/// </summary>
public sealed class ServerDetailLoaderHubTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerDetailLoaderHubTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        this.UseInMemoryHubs();
        foreach (var id in new[] { 7, 8 })
        {
            _handler.SetJsonResponse($"api/servers/{id}", new ServerDetailDto
            {
                Id = id,
                Name = $"web-{id}",
                Hostname = $"10.0.0.{id}",
                IpAddress = $"10.0.0.{id}",
                Status = ServerStatus.Online
            });
            _handler.SetJsonResponse($"api/monitoring/servers/{id}/metrics", new List<ServerMetricDto>());
        }
    }

    private InMemoryHubServer Servers => this.Hub("servers");

    private ServerDetailLoader NewLoader() => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        Services.GetRequiredService<NavigationManager>(),
        NullLogger<ServerDetailLoader>.Instance);

    // The loader adopts its connection in the background once the group is joined, and says so through
    // OnChanged: the test waits on that signal, not on the clock.
    private static Task AdoptedAsync(ServerDetailLoader loader, StateSignal changed, Func<HubConnection?, bool> hub) =>
        changed.WhenAsync(() => hub(loader.Hub), "the loader adopting its live connection");

    [Fact]
    public async Task ALoadedServer_JoinsItsGroup_AndTheConnectionBecomesTheLoaders()
    {
        await using var loader = NewLoader();
        var changes = 0;
        var changed = new StateSignal();
        loader.OnChanged += () => { Interlocked.Increment(ref changes); changed.Pulse(); };

        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        await AdoptedAsync(loader, changed, hub => hub is { State: HubConnectionState.Connected });

        Assert.Equal([7], Servers.IntArguments("JoinServerGroup"));
        Assert.True(Volatile.Read(ref changes) >= 2);
    }

    [Fact]
    public async Task AnotherServer_LeavesTheFirstGroup_AndReplacesTheConnection()
    {
        await using var loader = NewLoader();
        var changed = new StateSignal();
        loader.OnChanged += changed.Pulse;
        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        await AdoptedAsync(loader, changed, hub => hub is not null);
        var first = loader.Hub;

        await loader.EnsureLoadedAsync(8, Xunit.TestContext.Current.CancellationToken);
        await AdoptedAsync(loader, changed, hub => hub is not null && !ReferenceEquals(hub, first));
        await Servers.WhenInvokedAsync("LeaveServerGroup");

        Assert.Equal([7], Servers.IntArguments("LeaveServerGroup"));
        Assert.Equal([7, 8], Servers.IntArguments("JoinServerGroup"));
    }

    // R-16: leaving the server pages closes the live connection, which asks the hub to leave the group. A hub
    // slow to answer (a proxy, a mobile link) must not hold back the page of the same server the reader
    // returns to: the old connection is closed on its own, the new load does not wait for it.
    [Fact]
    public async Task ReturningToTheServer_DoesNotWaitForTheOldConnectionToLeaveItsGroup()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/servers/7/services");
        await using var loader = NewLoader();
        var changed = new StateSignal();
        loader.OnChanged += changed.Pulse;
        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        await AdoptedAsync(loader, changed, hub => hub is { State: HubConnectionState.Connected });

        using var slowLeave = Servers.Hold("LeaveServerGroup");
        nav.NavigateTo("/tasks/22358");
        await Servers.WhenInvokedAsync("LeaveServerGroup");
        nav.NavigateTo("/servers/7/services");

        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken)
            .WaitAsync(HangGuard.Limit, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(7, loader.Server?.Id);
        Assert.True(loader.InitialLoadCompleted);
        Assert.Equal(2, _handler.RequestsSnapshot().Count(r => r.Method == "GET" && r.Url.EndsWith("api/servers/7", StringComparison.Ordinal)));
    }

    // R-16: the old connection is closed in the background, so it can still deliver events for a while. Its
    // events are for the server that was left: they must neither change the server now shown nor reach the
    // sections listening to the loader.
    [Fact]
    public async Task TheOldConnection_StillOpen_DoesNotFeedTheNextServer()
    {
        await using var loader = NewLoader();
        var changed = new StateSignal();
        loader.OnChanged += changed.Pulse;
        var completed = 0;
        loader.OnTaskCompleted += _ =>
        {
            Interlocked.Increment(ref completed);
            return Task.CompletedTask;
        };
        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        await AdoptedAsync(loader, changed, hub => hub is { State: HubConnectionState.Connected });

        // The old connection stays open (its leave is held) and the next one cannot open: the only
        // connection that can still speak is the one of the server that was left.
        using var slowLeave = Servers.Hold("LeaveServerGroup");
        Servers.RefuseConnections = true;
        await loader.EnsureLoadedAsync(8, Xunit.TestContext.Current.CancellationToken);
        await Servers.WhenInvokedAsync("LeaveServerGroup");

        await Servers.PushAsync("ServerOffline", 7);
        await Servers.PushAsync("TaskCompleted", new TaskCompletedNotification { TaskId = 1, ServerId = 7 });

        Assert.Equal(8, loader.Server?.Id);
        Assert.Equal(ServerStatus.Online, loader.Server?.Status);
        Assert.Equal(0, Volatile.Read(ref completed));
        Assert.Equal(1, _handler.RequestsSnapshot().Count(r => r.Method == "GET" && r.Url.EndsWith("api/servers/7", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AfterAReconnect_TheServerGroupIsJoinedAgain()
    {
        await using var loader = NewLoader();
        var changed = new StateSignal();
        loader.OnChanged += changed.Pulse;
        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        await AdoptedAsync(loader, changed, hub => hub is { State: HubConnectionState.Connected });

        await Servers.DropAsync();

        await Servers.WhenInvokedAsync("JoinServerGroup", 2);
        Assert.Equal([7, 7], Servers.IntArguments("JoinServerGroup"));
    }
}
