// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Monitoring;

public static class MonitoringModuleExtensions
{
    public static IServiceCollection AddMonitoringModule(this IServiceCollection services)
    {
        services.AddScoped<IMonitoringRepository, MonitoringRepository>();
        services.AddScoped<IMonitoringService, MonitoringService>();
        return services;
    }
}
