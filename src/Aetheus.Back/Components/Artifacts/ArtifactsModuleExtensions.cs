// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Artifacts;

public static class ArtifactsModuleExtensions
{
    public static IServiceCollection AddArtifactsModule(this IServiceCollection services)
    {
        services.AddScoped<IArtifactRepository, ArtifactRepository>();
        services.AddScoped<IArtifactService, ArtifactService>();
        services.AddScoped<IArtifactRetentionService, ArtifactRetentionService>();
        services.AddScoped<IArtifactStorageMeasurementRepository, ArtifactStorageMeasurementRepository>();
        services.AddSingleton<IArtifactStorageService, ArtifactStorageService>();
        // Singleton like the storage service: sessions live on disk, so it holds no per-request state.
        services.AddSingleton<IChunkedArtifactUploadService, ChunkedArtifactUploadService>();
        services.AddHostedService<ArtifactCleanupService>();
        services.AddSingleton<ArtifactFileInventory>();
        services.AddHostedService<ArtifactStorageMonitorService>();
        return services;
    }
}
