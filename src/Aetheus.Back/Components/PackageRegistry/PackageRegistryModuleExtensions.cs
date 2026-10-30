// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.PackageRegistry;

public static class PackageRegistryModuleExtensions
{
    public static IServiceCollection AddPackageRegistryModule(this IServiceCollection services)
    {
        services.AddScoped<IPackageRegistryRepository, PackageRegistryRepository>();
        services.AddScoped<IPackageRegistryStorage, PackageRegistryStorage>();
        services.AddScoped<INuGetRegistryService, NuGetRegistryService>();
        services.AddScoped<INpmRegistryService, NpmRegistryService>();
        services.AddScoped<IMavenRegistryService, MavenRegistryService>();
        services.AddScoped<IPackageRegistryAdminService, PackageRegistryAdminService>();
        services.AddScoped<NuGetPackageInspector>();
        services.AddScoped<NpmPackageInspector>();
        services.AddSingleton<PackageRegistryPublishGate>();
        services.AddSingleton<PackageRegistryUploadGate>();
        services.AddHostedService<PackageRegistryStorageReconciler>();
        services.AddPackageRegistryReadPolicy();
        return services;
    }
}
