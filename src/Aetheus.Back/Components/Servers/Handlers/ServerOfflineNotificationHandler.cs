// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Servers.Handlers;

/// <summary>
/// Pushes a real-time toast payload over <see cref="AlertHub"/> when a server is marked offline.
/// Front-side admins listening on the <c>alerts</c> group will receive a <c>ServerOffline</c>
/// event carrying <see cref="ServerOfflineNotificationPayload"/>.
/// </summary>
public sealed class ServerOfflineNotificationHandler(IHubContext<AlertHub> hub)
    : IDomainEventHandler<ServerWentOfflineEvent>
{
    public async Task HandleAsync(ServerWentOfflineEvent domainEvent, CancellationToken ct = default)
    {
        var payload = new ServerOfflineNotificationPayload(
            domainEvent.ServerId,
            domainEvent.ServerName,
            domainEvent.LastHeartbeat,
            domainEvent.Reachability);

        await hub.Clients.Group(HubGroups.Alerts)
            .SendAsync("ServerOffline", payload, ct)
            .ConfigureAwait(false);
    }
}
