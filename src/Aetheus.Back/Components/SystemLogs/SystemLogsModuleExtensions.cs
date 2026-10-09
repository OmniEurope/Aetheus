// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.SystemLogs;

public static class SystemLogsModuleExtensions
{
    public static IServiceCollection AddSystemLogsModule(this IServiceCollection services)
    {
        services.AddSingleton<ISystemLogService, SystemLogService>();
        // PLAN-003 lot 27, R-455, decision of 2026-10-07: one collector, OmniEurope.Performance's, started
        // with the host so it hears the first request, whether or not the Aetheus.Telemetry export is on (the
        // export reads this same collector). Registered alone: the page is Aetheus's, not the package's.
        services.AddOmniPerformanceCollector(ApiPerformanceReport.Configure);
        // Recette R-181: the system logs page follows the log files over the admin hub.
        services.AddHostedService<SystemLogChangeBroadcaster>();
        // R-181: tells the open Performance pages when new samples arrived (coalesced, no timer poll).
        services.AddHostedService<ApiPerformanceChangeBroadcaster>();
        return services;
    }
}
