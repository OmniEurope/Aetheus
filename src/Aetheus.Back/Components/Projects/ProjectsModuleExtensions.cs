// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

public static class ProjectsModuleExtensions
{
    public static IServiceCollection AddProjectsModule(this IServiceCollection services)
    {
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<IProjectDeletionRepository, ProjectDeletionRepository>();
        services.AddScoped<IProjectDeletionService, ProjectDeletionService>();
        services.AddScoped<IProjectDataRetentionRepository, ProjectDataRetentionRepository>();
        services.AddScoped<IProjectDataRetentionService, ProjectDataRetentionService>();
        services.AddScoped<IProjectStorageRepository, ProjectStorageRepository>();
        services.AddScoped<IProjectStorageBudgetService, ProjectStorageBudgetService>();
        services.AddHostedService<ProjectStorageBudgetWorker>();
        services.AddScoped<IReclaimSpaceRepository, ReclaimSpaceRepository>();
        services.AddScoped<IReclaimSpaceService, ReclaimSpaceService>();
        services.AddSingleton<ReclaimSpaceTracker>();
        return services;
    }
}
