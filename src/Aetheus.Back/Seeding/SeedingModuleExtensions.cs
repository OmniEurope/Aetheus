// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Seeding;

/// <summary>
/// Registers the development and conformance seeders. They sit above the component modules because
/// they fill the database through several of them (artifacts, git, monitoring, pipelines).
/// </summary>
internal static class SeedingModuleExtensions
{
    public static IServiceCollection AddSeedingModule(this IServiceCollection services)
    {
        services.AddScoped<DeliveryPipelineTemplateSeeder>();
        services.AddScoped<DemoContentSeeder>();
        services.AddScoped<TotoConformanceSeeder>();
        return services;
    }
}
