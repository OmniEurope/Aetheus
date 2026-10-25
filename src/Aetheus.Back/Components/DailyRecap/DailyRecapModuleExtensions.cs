// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications.MailTemplates;

namespace Aetheus.Back.Components.DailyRecap;

public static class DailyRecapModuleExtensions
{
    public static IServiceCollection AddDailyRecapModule(this IServiceCollection services)
    {
        services.AddScoped<IDailyRecapRepository, DailyRecapRepository>();
        services.AddScoped<DailyRecapMailTimeZoneProvider>();
        services.AddScoped<MailTimeZoneResolver>(provider => provider.GetRequiredService<DailyRecapMailTimeZoneProvider>().GetAsync);
        services.AddScoped<DailyRecapCollector>();
        services.AddScoped<DailyRecapService>();
        services.AddScoped<IDailyRecapService>(provider => provider.GetRequiredService<DailyRecapService>());
        services.AddHostedService<DailyRecapSchedulerService>();
        return services;
    }
}
