// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.SystemLogs;

public static class SystemLogsModuleExtensions
{
    public static IServiceCollection AddSystemLogsModule(this IServiceCollection services)
    {
        services.AddSingleton<ISystemLogService, SystemLogService>();
        // Start the shared OmniEurope.Performance collector with the host so it observes the first
        // request, independently of telemetry export. Register the collector without the package
        // page because Aetheus owns this UI.
        services.AddOmniPerformanceCollector(ApiPerformanceReport.Configure);
        // The system logs page follows the log files over the admin hub.
        services.AddHostedService<SystemLogChangeBroadcaster>();
        // Tells the open Performance pages when new samples arrived (coalesced, no timer poll).
        services.AddHostedService<ApiPerformanceChangeBroadcaster>();
        return services;
    }
}
