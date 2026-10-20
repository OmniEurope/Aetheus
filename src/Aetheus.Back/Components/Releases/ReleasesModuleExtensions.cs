// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Releases;

public static class ReleasesModuleExtensions
{
    public static IServiceCollection AddReleasesModule(this IServiceCollection services)
    {
        services.AddScoped<IReleaseRepository, ReleaseRepository>();
        services.AddScoped<IReleaseService, ReleaseService>();
        services.AddScoped<IReleaseStatusRestoreService, ReleaseStatusRestoreService>();
        services.AddScoped<IReleaseDeploymentAnnouncer, ReleaseDeploymentAnnouncer>();
        services.AddScoped<IReleaseProvenanceRepository, ReleaseProvenanceRepository>();
        services.AddScoped<IReleaseProvenanceService, ReleaseProvenanceService>();
        services.AddScoped<IDomainEventHandler<PipelineRunCompletedEvent>, PipelineRunCompletedReleaseHandler>();
        services.AddScoped<IDomainEventHandler<RollbackDeploymentSucceededEvent>, RollbackDeploymentSucceededHandler>();
        services.AddHostedService<ReleaseScanService>();
        services.AddScoped<IReleaseRetentionRepository, ReleaseRetentionRepository>();
        services.AddHostedService<ReleaseRetentionService>();
        return services;
    }
}
