// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>Purges telemetry according to project retention settings, grouping applications by
/// duration to bound sweep passes.</summary>
internal static class AppDataRetentionSweep
{
    public static async Task<AppDataRetentionPurge> PurgeAsync(
        IAppDataRetentionRepository retention,
        IAppMetricRepository metrics,
        IAppLogRepository logs,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var apps = await retention.GetAllAsync(ct).ConfigureAwait(false);
        var raw = 0;
        foreach (var group in apps.GroupBy(app => app.Values.TelemetryDetailRetentionDays))
            raw += await metrics.PurgeRawOlderThanAsync(nowUtc.AddDays(-group.Key), Ids(group), ct).ConfigureAwait(false);
        var hourly = 0;
        foreach (var group in apps.GroupBy(app => app.Values.TelemetryAggregateRetentionMonths))
            hourly += await metrics.PurgeHourlyOlderThanAsync(nowUtc.AddMonths(-group.Key), Ids(group), ct).ConfigureAwait(false);
        var purgedLogs = 0;
        foreach (var group in apps.GroupBy(app => app.Values.AppLogRetentionDays))
            purgedLogs += await logs.PurgeOlderThanAsync(nowUtc.AddDays(-group.Key), Ids(group), ct).ConfigureAwait(false);
        return new AppDataRetentionPurge(raw, hourly, purgedLogs);
    }

    private static List<int> Ids(IEnumerable<AppDataRetention> apps) => [.. apps.Select(app => app.AppId)];
}

internal readonly record struct AppDataRetentionPurge(int MetricRaw, int MetricHourly, int Logs);
