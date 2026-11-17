// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Diagnostics;

namespace Aetheus.Back.Components.SystemLogs;

public static class SystemLogsModuleExtensions
{
    /// <summary>The most log text one system logs request reads: 64 MiB, four times the package's 16 MiB default,
    /// because a production day can fill a 100 MiB file and the page's window spans three days. The newest
    /// entries are kept when it is exceeded (<c>OmniLogs:MaxBytesRead</c> overrides it).</summary>
    internal const long MaxBytesRead = 64L * 1024 * 1024;

    public static IServiceCollection AddSystemLogsModule(this IServiceCollection services)
    {
        services.AddScoped<ISystemLogService, SystemLogService>();
        services.AddScoped<IDatabaseTableVolumeRepository, DatabaseTableVolumeRepository>();
        services.AddScoped<IDatabaseTableVolumeService, DatabaseTableVolumeService>();
        services.AddScoped<ISystemLogExclusionRepository, SystemLogExclusionRepository>();
        services.AddScoped<ISystemLogExclusionService, SystemLogExclusionService>();
        // OmniEurope.Diagnostics reads the log folder Aetheus writes (Logging:FileLog:Directory): the journal
        // (list, search, open, purge) and the change signal. Its own pages are not mapped, Aetheus owns this UI.
        // The package's exclusion rules are not used either: Aetheus keeps them per organization in the database.
        services.AddOmniLogs();
        // Defaults, applied after the OmniLogs section was bound: a value the host configured there wins.
        services.AddOptions<LogsOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            if (string.IsNullOrWhiteSpace(options.Directory))
                options.Directory = LogDirectory(configuration);
            if (configuration[$"{LogsServiceCollectionExtensions.ConfigurationSection}:{nameof(LogsOptions.MaxBytesRead)}"] is null)
                options.MaxBytesRead = MaxBytesRead;
        });
        // Start the shared OmniEurope.Diagnostics collector with the host so it observes the first
        // request, independently of telemetry export. Register the collector without the package
        // page because Aetheus owns this UI.
        services.AddOmniPerformanceCollector(ApiPerformanceReport.Configure);
        // The system logs page follows the log files over the admin hub.
        services.AddHostedService<SystemLogChangeBroadcaster>();
        // Tells the open Performance pages when new samples arrived (coalesced, no timer poll).
        services.AddHostedService<ApiPerformanceChangeBroadcaster>();
        return services;
    }

    /// <summary>The folder the file logger writes to and the journal reads, one definition for both.</summary>
    internal static string LogDirectory(IConfiguration configuration) =>
        configuration["Logging:FileLog:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "logs");
}
