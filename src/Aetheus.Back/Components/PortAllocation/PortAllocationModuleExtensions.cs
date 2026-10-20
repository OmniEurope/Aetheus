// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.PortAllocation;

public static class PortAllocationModuleExtensions
{
    public static IServiceCollection AddPortAllocationModule(this IServiceCollection services)
    {
        services.AddScoped<IPortAllocationRepository, PortAllocationRepository>();
        services.AddScoped<IPortAllocationService, PortAllocationService>();
        // Registers the application-port handler here to keep the ServerApps and PortRegistry
        // storage modules independent (ADR-039).
        services.AddScoped<
            Services.DomainEvents.IDomainEventHandler<ServerApps.Events.ServerAppPortChangedEvent>,
            ServerAppPortRegistryHandler>();
        return services;
    }
}
