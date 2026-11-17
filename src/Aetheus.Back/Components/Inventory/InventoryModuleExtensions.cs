// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Inventory;

/// <summary>What each project contains, read from its repositories and its last SBOM (PLAN-004 lot 2).
/// It sits above Git, whose events it hears, and is switched by the vulnerability watch options.</summary>
public static class InventoryModuleExtensions
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        services.AddSingleton<ProjectInventoryGate>();
        services.AddSingleton<IProjectInventoryTrigger, ProjectInventoryTrigger>();
        services.AddScoped<IProjectInventoryRepository, ProjectInventoryRepository>();
        services.AddScoped<IProjectSecurityUpdatesRepository, ProjectSecurityUpdatesRepository>();
        services.AddScoped<IProjectSourceReader, GitProjectSourceReader>();
        services.AddScoped<RepositoryInventoryReader>();
        services.AddScoped<IProjectInventoryService, ProjectInventoryService>();
        services.AddScoped<GitBranchInventoryHandler>();
        services.AddScoped<IDomainEventHandler<GitPushProcessedEvent>>(
            provider => provider.GetRequiredService<GitBranchInventoryHandler>());
        services.AddScoped<IDomainEventHandler<GitBranchMovedEvent>>(
            provider => provider.GetRequiredService<GitBranchInventoryHandler>());
        services.AddHostedService<ProjectInventoryRefreshService>();
        return services;
    }
}
