// SPDX-License-Identifier: EUPL-1.2
using System.Data;
using System.Globalization;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppWebAnalyticsRepository(AppDbContext db) : IAppWebAnalyticsRepository
{
    private readonly AppWebAnalyticsStorageRepository _storage = new(db);

    public async Task<(int Accepted, int Replayed)> RecordAsync(
        int appId,
        IReadOnlyList<AppWebAnalyticsIngestEvent> events,
        int sessionTimeoutMinutes,
        DateTime receivedAtUtc,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            IDbContextTransaction? transaction = null;
            try
            {
                if (db.Database.IsRelational())
                    transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
                        .ConfigureAwait(false);
                var result = await RecordBatchAsync(
                    appId, events, sessionTimeoutMinutes, receivedAtUtc, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                if (transaction is not null)
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch (Exception ex) when (attempt < 4 && IsConcurrencyConflict(ex))
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
            }
            finally
            {
                if (transaction is not null)
                    await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<(int Accepted, int Replayed)> RecordBatchAsync(
        int appId,
        IReadOnlyList<AppWebAnalyticsIngestEvent> events,
        int sessionTimeoutMinutes,
        DateTime receivedAtUtc,
        CancellationToken ct)
    {
        var batchIds = new HashSet<Guid>();
        var ordered = events.OrderBy(item => item.OccurredAtUtc)
            .Where(item => batchIds.Add(item.EventId))
            .ToList();
        var duplicateCount = events.Count - ordered.Count;
        var eventIds = ordered.Select(item => item.EventId).ToList();
        var existingIds = await db.AppAnalyticsEvents.AsNoTracking()
            .Where(item => item.MonitoredAppId == appId && eventIds.Contains(item.EventId))
            .Select(item => item.EventId)
            .ToListAsync(ct).ConfigureAwait(false);
        var existingIdSet = existingIds.ToHashSet();
        ordered.RemoveAll(item => existingIdSet.Contains(item.EventId));

        var pageViewEvents = ordered.Where(item => item.Kind == "page_view").ToList();
        // Heartbeats keep AppAnalyticsSession.LastSeenAtUtc fresh for the 5-minute "online" window
        // without counting as page views, so they share session/period preload with page views.
        var sessionEvents = ordered
            .Where(item => item.Kind is "page_view" or "heartbeat")
            .ToList();
        var sessionPseudonyms = sessionEvents.Select(item => item.SessionPseudonym).Distinct().ToList();
        var keyVersions = sessionEvents.Select(item => item.KeyVersion).Distinct().ToList();
        var earliestSessionFloor = sessionEvents.Count == 0
            ? receivedAtUtc
            : sessionEvents.Min(item => item.OccurredAtUtc).AddMinutes(-sessionTimeoutMinutes);
        await db.AppAnalyticsSessions
            .Where(item => item.MonitoredAppId == appId
                           && sessionPseudonyms.Contains(item.SessionPseudonym)
                           && keyVersions.Contains(item.KeyVersion)
                           && item.LastSeenAtUtc >= earliestSessionFloor)
            .LoadAsync(ct).ConfigureAwait(false);
        var returningPseudonyms = (await db.AppAnalyticsSessions.AsNoTracking()
                .Where(item => item.MonitoredAppId == appId
                               && sessionPseudonyms.Contains(item.SessionPseudonym)
                               && keyVersions.Contains(item.KeyVersion)
                               && item.LastSeenAtUtc < earliestSessionFloor)
                .Select(item => new { item.SessionPseudonym, item.KeyVersion })
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(item => (item.SessionPseudonym, item.KeyVersion))
            .ToHashSet();

        var periodStarts = sessionEvents.SelectMany(Periods)
            .Select(item => item.Start).Distinct().ToList();
        var periodPseudonyms = sessionEvents.SelectMany(Periods)
            .Select(item => item.Pseudonym)
            .Concat(sessionEvents.SelectMany(AuthenticatedPeriods).Select(item => item.Pseudonym))
            .Distinct().ToList();
        await db.AppAnalyticsPeriodIdentities
            .Where(item => item.MonitoredAppId == appId
                           && periodStarts.Contains(item.PeriodStartUtc)
                           && periodPseudonyms.Contains(item.Pseudonym))
            .LoadAsync(ct).ConfigureAwait(false);
        await db.AppAnalyticsAggregates
            .Where(item => item.MonitoredAppId == appId
                           && periodStarts.Contains(item.PeriodStartUtc))
            .LoadAsync(ct).ConfigureAwait(false);
        var days = pageViewEvents.Select(item => DateOnly.FromDateTime(item.OccurredAtUtc)).Distinct().ToList();
        var routes = pageViewEvents.Select(item => item.Route).Distinct().ToList();
        await db.AppAnalyticsPageAggregates
            .Where(item => item.MonitoredAppId == appId
                           && days.Contains(item.DayUtc)
                           && routes.Contains(item.Route))
            .LoadAsync(ct).ConfigureAwait(false);

        foreach (var analyticsEvent in ordered)
            RecordLoadedEvent(appId, analyticsEvent, sessionTimeoutMinutes, receivedAtUtc, returningPseudonyms);
        // Persisted accepted volume includes heartbeats so the rolling-hour cap counts them even
        // without raw event rows.
        if (ordered.Count > 0)
        {
            db.AppAnalyticsIngestVolumes.Add(new AppAnalyticsIngestVolume
            {
                MonitoredAppId = appId,
                ReceivedAtUtc = receivedAtUtc,
                Count = ordered.Count
            });
        }
        return (ordered.Count, duplicateCount + existingIds.Count);
    }

    private void RecordLoadedEvent(
        int appId,
        AppWebAnalyticsIngestEvent analyticsEvent,
        int sessionTimeoutMinutes,
        DateTime receivedAtUtc,
        HashSet<(string SessionPseudonym, int KeyVersion)> returningPseudonyms)
    {
        var isHeartbeat = string.Equals(analyticsEvent.Kind, "heartbeat", StringComparison.Ordinal);
        // Heartbeats update session timestamps and period identities without storing unused raw
        // rows. Replaying one is harmless because it reapplies the timestamp.
        if (!isHeartbeat)
        {
            db.AppAnalyticsEvents.Add(new AppAnalyticsEvent
            {
                MonitoredAppId = appId,
                EventId = analyticsEvent.EventId,
                OccurredAtUtc = analyticsEvent.OccurredAtUtc,
                Kind = analyticsEvent.Kind,
                Route = analyticsEvent.Route,
                DurationMs = analyticsEvent.DurationMs,
                ErrorType = analyticsEvent.ErrorType,
                SessionPseudonym = analyticsEvent.SessionPseudonym,
                Authenticated = analyticsEvent.AuthenticatedPseudonym is not null,
                KeyVersion = analyticsEvent.KeyVersion
            });
        }
        var isPageView = string.Equals(analyticsEvent.Kind, "page_view", StringComparison.Ordinal);
        if (!isPageView && !isHeartbeat)
            return;

        var session = ResolveSession(appId, analyticsEvent, sessionTimeoutMinutes, returningPseudonyms,
            out var newSession);
        if (analyticsEvent.OccurredAtUtc < session.StartedAtUtc)
            session.StartedAtUtc = analyticsEvent.OccurredAtUtc;
        if (analyticsEvent.OccurredAtUtc > session.LastSeenAtUtc)
            session.LastSeenAtUtc = analyticsEvent.OccurredAtUtc;
        if (isPageView)
            session.PageViewCount++;

        ApplyToPeriodAggregates(appId, analyticsEvent, receivedAtUtc, isPageView, newSession, session);

        if (isPageView)
            ApplyToPageAggregate(appId, analyticsEvent);
    }

    private AppAnalyticsSession ResolveSession(
        int appId,
        AppWebAnalyticsIngestEvent analyticsEvent,
        int sessionTimeoutMinutes,
        HashSet<(string SessionPseudonym, int KeyVersion)> returningPseudonyms,
        out bool newSession)
    {
        var floor = analyticsEvent.OccurredAtUtc.AddMinutes(-sessionTimeoutMinutes);
        var ceiling = analyticsEvent.OccurredAtUtc.AddMinutes(sessionTimeoutMinutes);
        var session = db.AppAnalyticsSessions.Local
            .Where(item => item.MonitoredAppId == appId
                           && item.SessionPseudonym == analyticsEvent.SessionPseudonym
                           && item.KeyVersion == analyticsEvent.KeyVersion
                           && item.LastSeenAtUtc >= floor
                           && item.StartedAtUtc <= ceiling)
            .OrderByDescending(item => item.LastSeenAtUtc)
            .FirstOrDefault();
        newSession = session is null;
        if (session is not null) return session;

        var returning = analyticsEvent.AuthenticatedPseudonym is not null
                        && (returningPseudonyms.Contains((
                                analyticsEvent.SessionPseudonym,
                                analyticsEvent.KeyVersion))
                            || db.AppAnalyticsSessions.Local.Any(item =>
                                item.MonitoredAppId == appId
                                && item.SessionPseudonym == analyticsEvent.SessionPseudonym
                                && item.KeyVersion == analyticsEvent.KeyVersion
                                && item.LastSeenAtUtc < floor));
        session = new AppAnalyticsSession
        {
            MonitoredAppId = appId,
            SessionPseudonym = analyticsEvent.SessionPseudonym,
            StartedAtUtc = analyticsEvent.OccurredAtUtc,
            LastSeenAtUtc = analyticsEvent.OccurredAtUtc,
            Authenticated = analyticsEvent.AuthenticatedPseudonym is not null,
            ReturningVisitor = returning,
            KeyVersion = analyticsEvent.KeyVersion
        };
        db.AppAnalyticsSessions.Add(session);
        return session;
    }

    private void ApplyToPeriodAggregates(
        int appId,
        AppWebAnalyticsIngestEvent analyticsEvent,
        DateTime receivedAtUtc,
        bool isPageView,
        bool newSession,
        AppAnalyticsSession session)
    {
        foreach (var (kind, start, pseudonym) in Periods(analyticsEvent))
        {
            var newIdentity = TrackPeriodIdentity(appId, kind, start, pseudonym, analyticsEvent);
            // A signed-in visitor shares the period identity of the anonymous events that precede
            // sign-in (same network, same period), so "first time this period" must be decided on
            // the authenticated pseudonym's own identity, not on the visitor identity.
            var newAuthenticatedIdentity = analyticsEvent.AuthenticatedPseudonym is { } authenticated
                && TrackPeriodIdentity(appId, AuthenticatedKindOf(kind), start, authenticated, analyticsEvent);

            var aggregate = db.AppAnalyticsAggregates.Local.FirstOrDefault(item =>
                item.MonitoredAppId == appId
                && item.PeriodKind == kind
                && item.PeriodStartUtc == start);
            if (aggregate is null)
            {
                aggregate = new AppAnalyticsAggregate
                {
                    MonitoredAppId = appId,
                    PeriodKind = kind,
                    PeriodStartUtc = start
                };
                db.AppAnalyticsAggregates.Add(aggregate);
            }
            if (isPageView)
                aggregate.PageViews++;
            if (newIdentity)
                aggregate.UniqueVisitors++;
            if (newAuthenticatedIdentity)
                aggregate.AuthenticatedUniqueVisitors++;
            if (newSession)
            {
                aggregate.Sessions++;
                if (session.ReturningVisitor)
                    aggregate.ReturningVisitors++;
            }
            aggregate.UpdatedAtUtc = receivedAtUtc;
        }
    }

    private void ApplyToPageAggregate(int appId, AppWebAnalyticsIngestEvent analyticsEvent)
    {
        var day = DateOnly.FromDateTime(analyticsEvent.OccurredAtUtc);
        var page = db.AppAnalyticsPageAggregates.Local.FirstOrDefault(item =>
            item.MonitoredAppId == appId && item.DayUtc == day && item.Route == analyticsEvent.Route);
        if (page is null)
        {
            page = new AppAnalyticsPageAggregate
            {
                MonitoredAppId = appId,
                DayUtc = day,
                Route = analyticsEvent.Route
            };
            db.AppAnalyticsPageAggregates.Add(page);
        }
        page.PageViews++;
    }

    private static IEnumerable<(AnalyticsPeriodKind Kind, DateOnly Start, string Pseudonym)> Periods(
        AppWebAnalyticsIngestEvent analyticsEvent)
    {
        var instant = analyticsEvent.OccurredAtUtc;
        yield return (AnalyticsPeriodKind.Day, DateOnly.FromDateTime(instant), analyticsEvent.DailyPseudonym);
        yield return (AnalyticsPeriodKind.Week, WeekStart(instant), analyticsEvent.WeeklyPseudonym);
        yield return (AnalyticsPeriodKind.Month, new DateOnly(instant.Year, instant.Month, 1),
            analyticsEvent.MonthlyPseudonym);
    }

    /// <summary>Records the identity as seen at the event's time; true when it is new for its period.</summary>
    private bool TrackPeriodIdentity(
        int appId,
        AnalyticsPeriodKind kind,
        DateOnly start,
        string pseudonym,
        AppWebAnalyticsIngestEvent analyticsEvent)
    {
        var identity = db.AppAnalyticsPeriodIdentities.Local.FirstOrDefault(item =>
            item.MonitoredAppId == appId
            && item.PeriodKind == kind
            && item.PeriodStartUtc == start
            && item.Pseudonym == pseudonym);
        var newIdentity = identity is null;
        if (identity is null)
        {
            identity = new AppAnalyticsPeriodIdentity
            {
                MonitoredAppId = appId,
                PeriodKind = kind,
                PeriodStartUtc = start,
                Pseudonym = pseudonym,
                FirstSeenAtUtc = analyticsEvent.OccurredAtUtc,
                KeyVersion = analyticsEvent.KeyVersion
            };
            db.AppAnalyticsPeriodIdentities.Add(identity);
        }
        identity.LastSeenAtUtc = analyticsEvent.OccurredAtUtc;
        return newIdentity;
    }

    private static AnalyticsPeriodKind AuthenticatedKindOf(AnalyticsPeriodKind kind) => kind switch
    {
        AnalyticsPeriodKind.Day => AnalyticsPeriodKind.AuthenticatedDay,
        AnalyticsPeriodKind.Week => AnalyticsPeriodKind.AuthenticatedWeek,
        AnalyticsPeriodKind.Month => AnalyticsPeriodKind.AuthenticatedMonth,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a visitor period kind.")
    };

    /// <summary>
    /// The authenticated identities of an event (preloaded with the visitor ones), keyed on its
    /// authenticated pseudonym with the same period starts as <see cref="Periods"/>. They only decide
    /// AuthenticatedUniqueVisitors.
    /// </summary>
    private static IEnumerable<(AnalyticsPeriodKind Kind, DateOnly Start, string Pseudonym)> AuthenticatedPeriods(
        AppWebAnalyticsIngestEvent analyticsEvent)
    {
        if (analyticsEvent.AuthenticatedPseudonym is not { } pseudonym)
            yield break;
        foreach (var (kind, start, _) in Periods(analyticsEvent))
            yield return (AuthenticatedKindOf(kind), start, pseudonym);
    }

    private static bool IsConcurrencyConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException postgres
                && postgres.SqlState is PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected
                    or PostgresErrorCodes.UniqueViolation)
                return true;
        }
        return false;
    }

    public Task<long> EstimateStorageBytesAsync(int appId, CancellationToken ct = default) =>
        _storage.EstimateStorageBytesAsync(appId, ct);

    public Task<WebAnalyticsTrimResult> TrimToAsync(int appId, long targetBytes, CancellationToken ct = default) =>
        _storage.TrimToAsync(appId, targetBytes, ct);

    public Task<IReadOnlyList<WebAnalyticsStorageBudget>> GetStorageBudgetsAsync(CancellationToken ct = default) =>
        _storage.GetStorageBudgetsAsync(ct);

    public Task<int> CountAcceptedSinceAsync(int appId, DateTime sinceUtc, CancellationToken ct = default) =>
        _storage.CountAcceptedSinceAsync(appId, sinceUtc, ct);

    public async Task<HashSet<string>> GetRouteNamesAsync(int appId, CancellationToken ct = default)
    {
        var routes = await db.AppAnalyticsPageAggregates.AsNoTracking()
            .Where(item => item.MonitoredAppId == appId)
            .Select(item => item.Route)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return routes.ToHashSet(StringComparer.Ordinal);
    }

    public async Task RecordRejectionAsync(
        int appId,
        string reasonCode,
        int count,
        DateTime occurredAtUtc,
        CancellationToken ct = default)
    {
        db.AppAnalyticsRejections.Add(new AppAnalyticsRejection
        {
            MonitoredAppId = appId,
            OccurredAtUtc = occurredAtUtc,
            ReasonCode = reasonCode,
            Count = count
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<AppWebAnalyticsSummaryDto> GetSummaryAsync(
        int appId,
        DateOnly todayUtc,
        DateOnly weekStartUtc,
        DateOnly monthStartUtc,
        DateOnly historyStartUtc,
        long storageBudgetBytes,
        long rejectedEvents,
        DateTime? lastIngestAtUtc,
        CancellationToken ct = default)
    {
        var aggregateRows = await db.AppAnalyticsAggregates.AsNoTracking()
            .Where(item => item.MonitoredAppId == appId
                           && (item.PeriodStartUtc >= historyStartUtc
                               || item.PeriodStartUtc == weekStartUtc
                               || item.PeriodStartUtc == monthStartUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        var pages = await db.AppAnalyticsPageAggregates.AsNoTracking()
            .Where(item => item.MonitoredAppId == appId && item.DayUtc >= monthStartUtc)
            .GroupBy(item => item.Route)
            .Select(group => new AppWebAnalyticsPageDto
            {
                Route = group.Key,
                PageViews = group.Sum(item => item.PageViews)
            })
            .OrderByDescending(item => item.PageViews)
            // Every route of the month, for the virtualized top pages list. Ingestion already
            // refuses routes past this per-app limit, so the list stays bounded.
            .Take(AppMonitoringDefaults.MaximumAnalyticsRoutesPerApp)
            .ToListAsync(ct).ConfigureAwait(false);
        var historyStartInstantUtc = historyStartUtc.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var browserSignals = await db.AppAnalyticsEvents.AsNoTracking()
            .Where(item => item.MonitoredAppId == appId
                           && item.OccurredAtUtc >= historyStartInstantUtc
                           && (item.Kind == "browser_performance" || item.Kind == "browser_error"))
            .Select(item => new { item.Kind, item.DurationMs })
            .ToListAsync(ct).ConfigureAwait(false);
        var browserDurations = browserSignals
            .Where(item => item.Kind == "browser_performance" && item.DurationMs.HasValue)
            .Select(item => item.DurationMs!.Value)
            .Order()
            .ToList();
        var p95Index = browserDurations.Count == 0
            ? -1
            : Math.Max(0, (int)Math.Ceiling(browserDurations.Count * 0.95) - 1);
        var storage = await EstimateStorageBytesAsync(appId, ct).ConfigureAwait(false);

        var day = aggregateRows.FirstOrDefault(item =>
            item.PeriodKind == AnalyticsPeriodKind.Day && item.PeriodStartUtc == todayUtc);
        var week = aggregateRows.FirstOrDefault(item =>
            item.PeriodKind == AnalyticsPeriodKind.Week && item.PeriodStartUtc == weekStartUtc);
        var month = aggregateRows.FirstOrDefault(item =>
            item.PeriodKind == AnalyticsPeriodKind.Month && item.PeriodStartUtc == monthStartUtc);
        // A day with zero events never gets an aggregate row, so filling only the present days would leave
        // a collection outage visually indistinguishable from "adjacent to the next day" - fill the gap
        // to zero instead, same as GetVisitorSeriesAsync already does for the visitor chart.
        var byDay = aggregateRows
            .Where(item => item.PeriodKind == AnalyticsPeriodKind.Day && item.PeriodStartUtc >= historyStartUtc)
            .ToDictionary(item => item.PeriodStartUtc);
        var dayCount = todayUtc.DayNumber - historyStartUtc.DayNumber + 1;
        var daily = Enumerable.Range(0, Math.Max(0, dayCount))
            .Select(offset => historyStartUtc.AddDays(offset))
            .Select(day => byDay.TryGetValue(day, out var item)
                ? new AppWebAnalyticsPointDto
                {
                    DayUtc = day,
                    UniqueVisitors = item.UniqueVisitors,
                    Sessions = item.Sessions,
                    ReturningVisitors = item.ReturningVisitors,
                    PageViews = item.PageViews
                }
                : new AppWebAnalyticsPointDto { DayUtc = day })
            .ToList();

        return new AppWebAnalyticsSummaryDto
        {
            UniqueVisitorsToday = day?.UniqueVisitors ?? 0,
            UniqueVisitorsThisWeek = week?.UniqueVisitors ?? 0,
            UniqueVisitorsThisMonth = month?.UniqueVisitors ?? 0,
            AuthenticatedUniqueThisMonth = month?.AuthenticatedUniqueVisitors ?? 0,
            SessionsThisMonth = month?.Sessions ?? 0,
            ReturningVisitorsThisMonth = month?.ReturningVisitors ?? 0,
            PageViewsThisMonth = month?.PageViews ?? 0,
            BrowserPerformanceSamplesLast30Days = browserDurations.Count,
            AverageBrowserNavigationDurationMs = browserDurations.Count == 0
                ? null
                : browserDurations.Average(),
            P95BrowserNavigationDurationMs = p95Index < 0 ? null : browserDurations[p95Index],
            BrowserErrorsLast30Days = browserSignals.Count(item => item.Kind == "browser_error"),
            LastIngestAtUtc = lastIngestAtUtc,
            RejectedEvents = rejectedEvents,
            EstimatedStorageBytes = storage,
            StorageBudgetBytes = storageBudgetBytes,
            StorageUsagePercent = storageBudgetBytes <= 0
                ? 0
                : (int)Math.Min(100, storage * 100 / storageBudgetBytes),
            Daily = daily,
            TopPages = pages
        };
    }

    public Task<WebAnalyticsPurgeResult> PurgeAsync(
        DateTime eventCutoffUtc,
        DateTime sessionCutoffUtc,
        DateOnly aggregateCutoffUtc,
        DateTime rejectionCutoffUtc,
        CancellationToken ct = default) =>
        _storage.PurgeAsync(eventCutoffUtc, sessionCutoffUtc, aggregateCutoffUtc, rejectionCutoffUtc, ct);

    internal static DateOnly WeekStart(DateTime instant)
    {
        var day = DateOnly.FromDateTime(instant);
        var offset = ((int)instant.DayOfWeek + 6) % 7;
        return day.AddDays(-offset);
    }
}
