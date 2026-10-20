// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.ServerApps.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.PortAllocation;

/// <summary>Records application ports through an upper-layer handler, keeping <c>ServerApps</c> and
/// <c>PortRegistry</c> independent. Strict dispatch propagates ownership conflicts to the caller
/// instead of leaving an application claiming a reserved port (ADR-039).</summary>
internal sealed class ServerAppPortRegistryHandler(IPortRegistryService registry)
    : IDomainEventHandler<ServerAppPortChangedEvent>
{
    public Task HandleAsync(ServerAppPortChangedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return registry.SyncOwnedPortAsync(
            domainEvent.ServerId,
            domainEvent.Port,
            PortRegistryService.ServerAppOwnerKey(domainEvent.ServerAppId),
            domainEvent.AppName,
            projectId: null,
            ct);
    }
}
