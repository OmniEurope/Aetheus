// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Hubs;

/// <summary>
/// Every open hub connection, by user. SignalR groups are joined once, when a page subscribes, and
/// never re-checked: a user whose role, organization membership or role permissions are revoked kept
/// receiving the broadcasts of every group joined before (audit SITE-SEC-002). When a user's
/// permissions change, <see cref="AbortUser"/> closes their group-bearing connections; the client
/// reconnects on its own and re-joins through the hub methods, which authorize against the new rights.
/// </summary>
public sealed class HubConnectionRegistry
{
    private readonly ConcurrentDictionary<string, (string UserId, Type HubType, HubCallerContext Context)> _connections = new();

    public void Track(Type hubType, HubCallerContext context)
    {
        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return;
        _connections[context.ConnectionId] = (userId, hubType, context);
    }

    public void Untrack(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>
    /// Aborts every connection of <paramref name="userId"/> except those to <see cref="UserHub"/>, which
    /// joins no group and carries the PermissionsChanged message itself. Returns how many were aborted.
    /// </summary>
    public int AbortUser(string userId)
    {
        var aborted = 0;
        foreach (var (connectionId, entry) in _connections)
        {
            if (entry.UserId != userId || entry.HubType == typeof(UserHub)) continue;
            if (!_connections.TryRemove(connectionId, out _)) continue;
            entry.Context.Abort();
            aborted++;
        }
        return aborted;
    }
}

/// <summary>Feeds <see cref="HubConnectionRegistry"/> from every hub's connect and disconnect.</summary>
public sealed class HubConnectionRegistryFilter(HubConnectionRegistry registry) : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await next(context).ConfigureAwait(false);
        registry.Track(context.Hub.GetType(), context.Context);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        registry.Untrack(context.Context.ConnectionId);
        await next(context, exception).ConfigureAwait(false);
    }
}
