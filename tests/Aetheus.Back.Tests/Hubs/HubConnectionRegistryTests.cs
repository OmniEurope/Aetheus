// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Aetheus.Back.Tests.Hubs;

/// <summary>SITE-SEC-002: a permission change closes the connections whose groups were joined under the old rights.</summary>
public class HubConnectionRegistryTests
{
    private static HubCallerContext Connection(string connectionId, string? userId)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(connectionId);
        var claims = userId is null ? [] : new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        return context;
    }

    [Fact]
    public void AbortUser_ClosesTheUsersGroupConnections_AndKeepsUserHubAndOtherUsers()
    {
        var registry = new HubConnectionRegistry();
        var servers = Connection("c-servers", "7");
        var entities = Connection("c-entities", "7");
        var userChannel = Connection("c-user", "7");
        var otherUser = Connection("c-other", "8");
        registry.Track(typeof(ServerHub), servers);
        registry.Track(typeof(EntityHub), entities);
        registry.Track(typeof(UserHub), userChannel);
        registry.Track(typeof(ServerHub), otherUser);

        var aborted = registry.AbortUser("7");

        Assert.Equal(2, aborted);
        servers.Received(1).Abort();
        entities.Received(1).Abort();
        userChannel.DidNotReceive().Abort();
        otherUser.DidNotReceive().Abort();
    }

    [Fact]
    public void AbortUser_IgnoresDisconnectedAndAnonymousConnections()
    {
        var registry = new HubConnectionRegistry();
        var gone = Connection("c-gone", "7");
        var anonymous = Connection("c-anon", null);
        registry.Track(typeof(ServerHub), gone);
        registry.Track(typeof(ServerHub), anonymous);
        registry.Untrack("c-gone");

        Assert.Equal(0, registry.AbortUser("7"));
        gone.DidNotReceive().Abort();
        anonymous.DidNotReceive().Abort();
    }

    [Fact]
    public async Task NotifyPermissionsChanged_SendsTheMessage_ThenClosesTheUsersGroupConnections()
    {
        var registry = new HubConnectionRegistry();
        var servers = Connection("c-servers", "7");
        registry.Track(typeof(ServerHub), servers);
        var hub = Substitute.For<IHubContext<UserHub>>();
        var client = Substitute.For<IClientProxy>();
        hub.Clients.User("7").Returns(client);

        await new UserChangeNotifier(hub, registry).NotifyPermissionsChangedAsync(7, "roles", TestContext.Current.CancellationToken);

        await client.Received(1).SendCoreAsync("PermissionsChanged", Arg.Is<object?[]>(a => (string?)a[0] == "roles"), Arg.Any<CancellationToken>());
        servers.Received(1).Abort();
    }
}
