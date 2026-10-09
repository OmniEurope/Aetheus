// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>
/// Recette R2-122: the telemetry, web-analytics and probe history of a project's monitored apps.
/// These tables grow without bound, so they are counted and deleted table by table, in batches,
/// before the apps themselves (whose own cascade would otherwise remove them in one statement).
/// </summary>
internal static class ProjectDeletionTelemetry
{
    internal static async Task<int> CountAsync(AppDbContext db, IReadOnlyList<int> appIds, CancellationToken ct)
    {
        if (appIds.Count == 0) return 0;
        var total = 0;
        total += await db.AppHealthSamples.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppHealthHourly.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppMetricSamples.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppMetricHourly.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppMetricNames.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppMetricThresholds.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppLogEntries.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppErrorEvents.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppVisitorIdentities.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppAnalyticsEvents.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppAnalyticsSessions.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppAnalyticsAggregates.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppAnalyticsPageAggregates.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppAnalyticsIngestVolumes.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppAnalyticsPeriodIdentities.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        total += await db.AppAnalyticsRejections.CountAsync(item => appIds.Contains(item.MonitoredAppId), ct).ConfigureAwait(false);
        return total;
    }

    internal static async Task DeleteAsync(
        ProjectDeletionRepository repository, AppDbContext db, IReadOnlyList<int> appIds, CancellationToken ct)
    {
        if (appIds.Count == 0) return;
        await repository.DeleteInBatchesAsync(db.AppHealthSamples.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppHealthHourly.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppMetricSamples.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppMetricHourly.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppMetricThresholds.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppMetricNames.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppLogEntries.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppErrorEvents.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppAnalyticsEvents.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppAnalyticsSessions.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppAnalyticsAggregates.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppAnalyticsPageAggregates.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppAnalyticsIngestVolumes.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppAnalyticsPeriodIdentities.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppAnalyticsRejections.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
        await repository.DeleteInBatchesAsync(db.AppVisitorIdentities.Where(item => appIds.Contains(item.MonitoredAppId)), item => item.Id, ct).ConfigureAwait(false);
    }
}
