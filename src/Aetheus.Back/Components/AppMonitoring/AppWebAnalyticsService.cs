// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring.Ingest;
using Aetheus.Back.Components.Notifications;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppWebAnalyticsService(
    IAppWebAnalyticsRepository repository,
    IAppMonitoringRepository apps,
    INotificationService notifications,
    AppIngestGate ingestGate,
    IAppTelemetryChangePublisher telemetryChanges,
    IMemoryCache cache,
    IConfiguration configuration,
    TimeProvider timeProvider) : IAppWebAnalyticsService
{
    public async Task<WebAnalyticsIngestOutcome> IngestAsync(
        int appId,
        IReadOnlyList<AppWebAnalyticsIngestEvent> events,
        CancellationToken ct = default)
    {
        var outcome = await StoreAsync(appId, events, ct).ConfigureAwait(false);
        // R-469: the open Visitors tab re-reads its figures and the date of the last event, as the Logs
        // and Errors tabs do after an OTLP batch. Pushed once the per-app gate is released.
        if (outcome.Accepted > 0)
            await telemetryChanges.PublishAsync(appId, ct).ConfigureAwait(false);
        return outcome;
    }

    private async Task<WebAnalyticsIngestOutcome> StoreAsync(
        int appId,
        IReadOnlyList<AppWebAnalyticsIngestEvent> events,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var gate = ingestGate.ForAnalytics(appId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var app = await apps.GetAppForUpdateAsync(appId, ct).ConfigureAwait(false);
            if (app is null)
                return new WebAnalyticsIngestOutcome(0, 0, events.Count);

            var capped = events.Take(AppMonitoringDefaults.MaximumAnalyticsBatchSize).ToList();
            var rejected = events.Count - capped.Count;
            var invalid = capped.RemoveAll(item => !IsAllowed(item, app, now));
            rejected += invalid;
            var routes = await LoadRouteNamesAsync(appId, ct).ConfigureAwait(false);
            var knownRoutes = routes.Count;
            var cardinalityRejected = capped.RemoveAll(item =>
            {
                if (routes.Contains(item.Route))
                    return false;
                if (routes.Count >= AppMonitoringDefaults.MaximumAnalyticsRoutesPerApp)
                    return true;
                routes.Add(item.Route);
                return false;
            });
            rejected += cardinalityRejected;
            if (rejected > 0)
            {
                app.AnalyticsRejectedCount += rejected;
                await repository.RecordRejectionAsync(
                    appId,
                    cardinalityRejected > 0 ? "cardinality" : "policy",
                    rejected,
                    now,
                    ct).ConfigureAwait(false);
            }

            if (routes.Count > knownRoutes)
                cache.Set(RouteNamesCacheKey(appId), routes, IngestSnapshotLifetime);
            var storageBytes = await EstimateStorageBytesAsync(appId, ct).ConfigureAwait(false);
            var budgetBytes = BudgetOf(app.AnalyticsStorageBudgetBytes);
            var usagePercent = (int)Math.Min(100, storageBytes * 100 / budgetBytes);
            var alertLevel = usagePercent >= 95 ? 95 : usagePercent >= 85 ? 85 : usagePercent >= 70 ? 70 : 0;
            if (alertLevel > app.AnalyticsQuotaAlertLevel)
            {
                app.AnalyticsQuotaAlertLevel = alertLevel;
                await notifications.SendEventAsync("app.analytics.quota", new
                {
                    MonitoredAppId = appId,
                    // The owning project: the AI tasks and the per-user notifications of an event are
                    // those of the project it is about (AiEventOwner, RecordProjectEventAsync).
                    app.ProjectId,
                    UsagePercent = usagePercent,
                    Threshold = alertLevel
                }, ct).ConfigureAwait(false);
            }

            // Recette R2-007: the audience rolls instead of refusing. At the budget, the oldest raw rows of
            // the app go until it is back under RollTargetPercent of it, and the batch is accepted.
            // Audit follow-up: only under the rolling-hour volume cap. Past it the anonymous ingest is a
            // flood that would erase the app's history, so the batch is refused and nothing is deleted.
            if (storageBytes >= budgetBytes
                && await RefuseFloodOrRollAsync(app, capped.Count, budgetBytes, now, ct).ConfigureAwait(false))
                return new WebAnalyticsIngestOutcome(0, 0, rejected + capped.Count);

            var sessionTimeout = Math.Clamp(
                configuration.GetValue(
                    "AppMonitoring:Analytics:SessionTimeoutMinutes",
                    AppMonitoringDefaults.DefaultAnalyticsSessionTimeoutMinutes),
                AppMonitoringDefaults.MinimumAnalyticsSessionTimeoutMinutes,
                AppMonitoringDefaults.MaximumAnalyticsSessionTimeoutMinutes);
            var (accepted, replayed) = await repository.RecordAsync(
                appId,
                capped,
                sessionTimeout,
                now,
                ct).ConfigureAwait(false);
            if (replayed > 0)
            {
                app.AnalyticsRejectedCount += replayed;
                await repository.RecordRejectionAsync(appId, "replay", replayed, now, ct).ConfigureAwait(false);
            }
            if (accepted > 0)
                app.AnalyticsLastIngestAt = now;
            await apps.SaveChangesAsync(ct).ConfigureAwait(false);
            return new WebAnalyticsIngestOutcome(accepted, replayed, rejected);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Recette R-487: how long the two figures every batch read under the lock are trusted. The route
    /// set was a DISTINCT over the page aggregates and the storage estimate six counts, both paid by
    /// each call. The route set only grows through this ingestion, which writes it back; the estimate
    /// is at most a minute behind, so the quota may be passed by what a minute of events weighs.
    /// </summary>
    internal static readonly TimeSpan IngestSnapshotLifetime = TimeSpan.FromSeconds(60);

    private static string RouteNamesCacheKey(int appId) => $"analytics-routes:{appId}";

    private static string StorageCacheKey(int appId) => $"analytics-storage:{appId}";

    /// <summary>A working copy of the app's route set; the caller writes it back when it grew.</summary>
    private async Task<HashSet<string>> LoadRouteNamesAsync(int appId, CancellationToken ct)
    {
        if (cache.TryGetValue<HashSet<string>>(RouteNamesCacheKey(appId), out var cached) && cached is not null)
            return new HashSet<string>(cached, StringComparer.Ordinal);
        var routes = await repository.GetRouteNamesAsync(appId, ct).ConfigureAwait(false);
        cache.Set(RouteNamesCacheKey(appId), new HashSet<string>(routes, StringComparer.Ordinal), IngestSnapshotLifetime);
        return routes;
    }

    /// <summary>Recette R2-007: the share of its budget an app is brought back under when it reaches it.</summary>
    internal const int RollTargetPercent = 90;

    /// <summary>
    /// Audit R2-007 follow-up, for an app at its budget: refuses the batch as a flood (reason
    /// <c>flood</c>, nothing deleted) when it would take the app past its rolling-hour cap, and
    /// otherwise rolls the oldest rows off so the batch is accepted. Returns whether it was refused.
    /// </summary>
    private async Task<bool> RefuseFloodOrRollAsync(
        Aetheus.Back.Data.Entities.MonitoredApp app,
        int batchSize,
        long budgetBytes,
        DateTime now,
        CancellationToken ct)
    {
        if (batchSize > 0 && await ExceedsRollingCapAsync(app.Id, batchSize, now, ct).ConfigureAwait(false))
        {
            app.AnalyticsRejectedCount += batchSize;
            await repository.RecordRejectionAsync(app.Id, "flood", batchSize, now, ct).ConfigureAwait(false);
            await apps.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        await RollAsync(app.Id, budgetBytes, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Audit R2-007 follow-up: whether accepting <paramref name="batchSize"/> more events would take the
    /// app past its rolling-hour cap. The hour is read from the volumes the batches saved in the shared
    /// database, so both blue-green colours count against the same cap.
    /// </summary>
    private async Task<bool> ExceedsRollingCapAsync(int appId, int batchSize, DateTime now, CancellationToken ct)
    {
        var cap = Math.Clamp(
            configuration.GetValue(
                "AppMonitoring:Analytics:RollingEventsPerHour",
                AppMonitoringDefaults.DefaultAnalyticsRollingEventsPerHour),
            AppMonitoringDefaults.MinimumAnalyticsRollingEventsPerHour,
            AppMonitoringDefaults.MaximumAnalyticsRollingEventsPerHour);
        var lastHour = await repository.CountAcceptedSinceAsync(appId, now.AddHours(-1), ct).ConfigureAwait(false);
        return (long)lastHour + batchSize > cap;
    }

    private static long BudgetOf(long configuredBytes) => configuredBytes > 0
        ? configuredBytes
        : AppMonitoringDefaults.DefaultAnalyticsStorageBudgetBytes;

    /// <summary>
    /// Recette R2-007: brings one app back under <see cref="RollTargetPercent"/> of its budget (oldest raw
    /// events first, then sessions, then period identities) and refreshes the cached estimate with what
    /// remains, so the next batches do not roll again on a stale figure.
    /// </summary>
    private async Task<int> RollAsync(int appId, long budgetBytes, CancellationToken ct)
    {
        var trimmed = await repository.TrimToAsync(appId, budgetBytes * RollTargetPercent / 100, ct)
            .ConfigureAwait(false);
        cache.Set(StorageCacheKey(appId), trimmed.RemainingBytes, IngestSnapshotLifetime);
        return trimmed.Total;
    }

    /// <summary>
    /// Recette R2-007: the hourly sweep's pass. Every app whose audience data reached its budget is rolled
    /// back under it, under the same per-app gate as the ingestion, so a batch never reads an estimate
    /// the roll is changing. Returns the rows removed.
    /// </summary>
    public async Task<int> RollStorageAsync(CancellationToken ct = default)
    {
        var removed = 0;
        foreach (var budget in await repository.GetStorageBudgetsAsync(ct).ConfigureAwait(false))
        {
            var gate = ingestGate.ForAnalytics(budget.AppId);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var budgetBytes = BudgetOf(budget.BudgetBytes);
                var storageBytes = await repository.EstimateStorageBytesAsync(budget.AppId, ct).ConfigureAwait(false);
                if (storageBytes >= budgetBytes)
                    removed += await RollAsync(budget.AppId, budgetBytes, ct).ConfigureAwait(false);
                else
                    cache.Set(StorageCacheKey(budget.AppId), storageBytes, IngestSnapshotLifetime);
            }
            finally
            {
                gate.Release();
            }
        }
        return removed;
    }

    private async Task<long> EstimateStorageBytesAsync(int appId, CancellationToken ct)
    {
        if (cache.TryGetValue<long>(StorageCacheKey(appId), out var cached)) return cached;
        var bytes = await repository.EstimateStorageBytesAsync(appId, ct).ConfigureAwait(false);
        cache.Set(StorageCacheKey(appId), bytes, IngestSnapshotLifetime);
        return bytes;
    }

    public async Task<AppWebAnalyticsSummaryDto?> GetSummaryAsync(
        int appId,
        int days,
        CancellationToken ct = default)
    {
        var app = await apps.GetAppForUpdateAsync(appId, ct).ConfigureAwait(false);
        if (app is null)
            return null;

        days = Math.Clamp(days, 1, 90);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        return await repository.GetSummaryAsync(
            appId,
            today,
            AppWebAnalyticsRepository.WeekStart(now),
            new DateOnly(now.Year, now.Month, 1),
            today.AddDays(-(days - 1)),
            BudgetOf(app.AnalyticsStorageBudgetBytes),
            app.AnalyticsRejectedCount,
            app.AnalyticsLastIngestAt,
            ct).ConfigureAwait(false);
    }

    private static bool IsAllowed(
        AppWebAnalyticsIngestEvent item,
        Aetheus.Back.Data.Entities.MonitoredApp app,
        DateTime now) =>
        HasValidEnvelope(item, app)
        && HasAcceptedKey(item, app, now)
        && IsSupportedPayload(item)
        && IsCurrent(item, now)
        && IsSafeRoute(item.Route);

    private static bool HasValidEnvelope(
        AppWebAnalyticsIngestEvent item,
        Aetheus.Back.Data.Entities.MonitoredApp app) =>
        app.AnalyticsEnabled
        && item.SchemaVersion == 1
        && item.EventId != Guid.Empty
        && item.ApplicationId == app.Id;

    private static bool HasAcceptedKey(
        AppWebAnalyticsIngestEvent item,
        Aetheus.Back.Data.Entities.MonitoredApp app,
        DateTime now) =>
        item.KeyVersion > 0
        && item.KeyVersion <= app.AnalyticsPseudonymKeyVersion
        && (item.KeyVersion == app.AnalyticsPseudonymKeyVersion
            || app.AnalyticsPseudonymKeyCreatedAt is { } createdAt && createdAt > now.AddDays(-31));

    private static bool IsCurrent(AppWebAnalyticsIngestEvent item, DateTime now) =>
        item.OccurredAtUtc >= now.AddMinutes(-5)
        && item.OccurredAtUtc <= now.AddMinutes(5);

    private static bool IsSafeRoute(string route) =>
        route.StartsWith("/", StringComparison.Ordinal)
        && !route.Contains('?', StringComparison.Ordinal)
        && !route.Contains('#', StringComparison.Ordinal)
        && !route.Contains('@', StringComparison.Ordinal);

    private static bool IsSupportedPayload(AppWebAnalyticsIngestEvent item) =>
        item.Kind switch
        {
            "page_view" or "heartbeat" => item.DurationMs is null && item.ErrorType is null,
            "browser_performance" => item.DurationMs is >= 0 and <= 300_000
                                     && item.ErrorType is null,
            "browser_error" => item.DurationMs is null
                               && item.ErrorType is "script_error"
                                   or "unhandled_rejection",
            _ => false
        };
}
