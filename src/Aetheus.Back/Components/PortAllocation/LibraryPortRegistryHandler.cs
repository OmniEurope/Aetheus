// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VariableLibraries.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.PortAllocation;

/// <summary>Registers a library's <c>PORT_*</c> entries through an upper-layer handler, keeping
/// <c>VariableLibraries</c> and <c>PortRegistry</c> independent. Strict dispatch propagates ownership
/// conflicts to the writer instead of leaving a library naming a port the registry says is taken (ADR-039).</summary>
internal sealed class LibraryPortRegistryHandler(LibraryPortRegistrar registrar)
    : IDomainEventHandler<VariableLibraryPortsChangedEvent>
{
    public Task HandleAsync(VariableLibraryPortsChangedEvent domainEvent, CancellationToken ct = default) =>
        registrar.ApplyAsync(domainEvent, ct);
}
