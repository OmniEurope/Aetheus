// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>Rows the storage budget removed from one application's audience data.</summary>
public readonly record struct WebAnalyticsTrimResult(int Events, int Sessions, int PeriodIdentities, long RemainingBytes)
{
    public int Total => Events + Sessions + PeriodIdentities;
}

/// <summary>An application holding audience data, with the storage budget it is held to.</summary>
public readonly record struct WebAnalyticsStorageBudget(int AppId, long BudgetBytes);

/// <summary> The storage side of the audience data: what it weighs, the age-based purge, and the
/// roll-off that keeps an application under its budget. Held by <see
/// cref="AppWebAnalyticsRepository"/> on the same context, so the recording code and this one see
/// the same rows. </summary>
internal sealed class AppWebAnalyticsStorageRepository(AppDbContext db)
{
    internal const int EventEstimatedBytes = 480;
    internal const int SessionEstimatedBytes = 320;
    internal const int IdentityEstimatedBytes = 240;
    internal const int AggregateEstimatedBytes = 192;
    internal const int PageEstimatedBytes = 256;

    /// <summary>Storage estimates exclude separately retained rejection counters so rejecting
    /// events does not increase the budgeted data size.</summary>
    public async Task<long> EstimateStorageBytesAsync(int appId, CancellationToken ct)
    {
        var events = await db.AppAnalyticsEvents.CountAsync(item => item.MonitoredAppId == appId, ct)
            .ConfigureAwait(false);
        var sessions = await db.AppAnalyticsSessions.CountAsync(item => item.MonitoredAppId == appId, ct)
            .ConfigureAwait(false);
        var identities = await db.AppAnalyticsPeriodIdentities.CountAsync(item => item.MonitoredAppId == appId, ct)
            .ConfigureAwait(false);
        var aggregates = await db.AppAnalyticsAggregates.CountAsync(item => item.MonitoredAppId == appId, ct)
            .ConfigureAwait(false);
        var pages = await db.AppAnalyticsPageAggregates.CountAsync(item => item.MonitoredAppId == appId, ct)
            .ConfigureAwait(false);
        return (long)events * EventEstimatedBytes
               + (long)sessions * SessionEstimatedBytes
               + (long)identities * IdentityEstimatedBytes
               + (long)aggregates * AggregateEstimatedBytes
               + (long)pages * PageEstimatedBytes;
    }

    /// <summary> Brings an application's audience data down to <paramref name="targetBytes"/> by
    /// deleting its oldest raw events first, then, when the events alone do not free enough, its
    /// oldest sessions, then its oldest period identities. The daily, weekly and monthly aggregates
    /// and the page counts are the history the tab draws and are never removed here (the age-based
    /// purge owns them). </summary>
    public async Task<WebAnalyticsTrimResult> TrimToAsync(int appId, long targetBytes, CancellationToken ct)
    {
        var remaining = await EstimateStorageBytesAsync(appId, ct).ConfigureAwait(false);
        var events = 0;
        var sessions = 0;
        var identities = 0;

        if (remaining > targetBytes)
        {
            var oldest = db.AppAnalyticsEvents
                .Where(item => item.MonitoredAppId == appId)
                .OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.Id)
                .Select(item => item.Id)
                .Take(RowsToFree(remaining - targetBytes, EventEstimatedBytes));
            events = await DeleteAsync(db.AppAnalyticsEvents.Where(item => oldest.Contains(item.Id)), ct)
                .ConfigureAwait(false);
            remaining -= (long)events * EventEstimatedBytes;
        }

        if (remaining > targetBytes)
        {
            var oldest = db.AppAnalyticsSessions
                .Where(item => item.MonitoredAppId == appId)
                .OrderBy(item => item.LastSeenAtUtc).ThenBy(item => item.Id)
                .Select(item => item.Id)
                .Take(RowsToFree(remaining - targetBytes, SessionEstimatedBytes));
            sessions = await DeleteAsync(db.AppAnalyticsSessions.Where(item => oldest.Contains(item.Id)), ct)
                .ConfigureAwait(false);
            remaining -= (long)sessions * SessionEstimatedBytes;
        }

        if (remaining > targetBytes)
        {
            var oldest = db.AppAnalyticsPeriodIdentities
                .Where(item => item.MonitoredAppId == appId)
                .OrderBy(item => item.LastSeenAtUtc).ThenBy(item => item.Id)
                .Select(item => item.Id)
                .Take(RowsToFree(remaining - targetBytes, IdentityEstimatedBytes));
            identities = await DeleteAsync(
                db.AppAnalyticsPeriodIdentities.Where(item => oldest.Contains(item.Id)), ct).ConfigureAwait(false);
            remaining -= (long)identities * IdentityEstimatedBytes;
        }

        return new WebAnalyticsTrimResult(events, sessions, identities, Math.Max(0, remaining));
    }

    /// <summary>The applications holding raw audience rows (events, sessions or identities), with their budget.</summary>
    public async Task<IReadOnlyList<WebAnalyticsStorageBudget>> GetStorageBudgetsAsync(CancellationToken ct)
    {
        var budgets = await db.MonitoredApps.AsNoTracking()
            .Where(app => db.AppAnalyticsEvents.Any(item => item.MonitoredAppId == app.Id)
                          || db.AppAnalyticsSessions.Any(item => item.MonitoredAppId == app.Id)
                          || db.AppAnalyticsPeriodIdentities.Any(item => item.MonitoredAppId == app.Id))
            .OrderBy(app => app.Id)
            .Select(app => new { app.Id, app.AnalyticsStorageBudgetBytes })
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. budgets.Select(item => new WebAnalyticsStorageBudget(item.Id, item.AnalyticsStorageBudgetBytes))];
    }

    /// <summary>Counts accepted events since <paramref name="sinceUtc"/>.</summary>
    public async Task<int> CountAcceptedSinceAsync(int appId, DateTime sinceUtc, CancellationToken ct) =>
        await db.AppAnalyticsIngestVolumes.AsNoTracking()
            .Where(item => item.MonitoredAppId == appId && item.ReceivedAtUtc >= sinceUtc)
            .SumAsync(item => item.Count, ct).ConfigureAwait(false);

    public async Task<WebAnalyticsPurgeResult> PurgeAsync(
        DateTime eventCutoffUtc,
        DateTime sessionCutoffUtc,
        DateOnly aggregateCutoffUtc,
        DateTime rejectionCutoffUtc,
        CancellationToken ct)
    {
        var events = await DeleteAsync(db.AppAnalyticsEvents.Where(item => item.OccurredAtUtc < eventCutoffUtc), ct)
            .ConfigureAwait(false);
        var sessions = await DeleteAsync(db.AppAnalyticsSessions.Where(item => item.LastSeenAtUtc < sessionCutoffUtc), ct)
            .ConfigureAwait(false);
        var identities = await DeleteAsync(
            db.AppAnalyticsPeriodIdentities.Where(item => item.LastSeenAtUtc < sessionCutoffUtc),
            ct).ConfigureAwait(false);
        var aggregates = await DeleteAsync(
            db.AppAnalyticsAggregates.Where(item => item.PeriodStartUtc < aggregateCutoffUtc),
            ct).ConfigureAwait(false);
        var pages = await DeleteAsync(
            db.AppAnalyticsPageAggregates.Where(item => item.DayUtc < aggregateCutoffUtc),
            ct).ConfigureAwait(false);
        var rejections = await DeleteAsync(
            db.AppAnalyticsRejections.Where(item => item.OccurredAtUtc < rejectionCutoffUtc),
            ct).ConfigureAwait(false);
        // The accepted volumes are only read over the last hour; they go with the rejections.
        var volumes = await DeleteAsync(
            db.AppAnalyticsIngestVolumes.Where(item => item.ReceivedAtUtc < rejectionCutoffUtc),
            ct).ConfigureAwait(false);
        return new WebAnalyticsPurgeResult(events, sessions, identities, aggregates, pages, rejections, volumes);
    }

    /// <summary>How many rows of <paramref name="rowBytes"/> free <paramref name="excessBytes"/>, rounded up.</summary>
    private static int RowsToFree(long excessBytes, int rowBytes) =>
        (int)Math.Min(int.MaxValue, (excessBytes + rowBytes - 1) / rowBytes);

    private async Task<int> DeleteAsync<TEntity>(IQueryable<TEntity> query, CancellationToken ct)
        where TEntity : class
    {
        if (db.Database.IsRelational())
            return await query.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var rows = await query.ToListAsync(ct).ConfigureAwait(false);
        db.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }
}
