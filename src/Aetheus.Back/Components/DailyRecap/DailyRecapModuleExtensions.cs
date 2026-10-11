// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

public static class DailyRecapModuleExtensions
{
    public static IServiceCollection AddDailyRecapModule(this IServiceCollection services)
    {
        services.AddScoped<IDailyRecapRepository, DailyRecapRepository>();
        services.AddScoped<DailyRecapCollector>();
        services.AddScoped<DailyRecapService>();
        services.AddScoped<IDailyRecapService>(provider => provider.GetRequiredService<DailyRecapService>());
        services.AddHostedService<DailyRecapSchedulerService>();
        return services;
    }
}
