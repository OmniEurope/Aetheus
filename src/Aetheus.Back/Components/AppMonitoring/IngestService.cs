// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// OTLP ingestion pipeline (ADR-021 phases 2-4): resolves the per-app ingest key (cached, incl. negative
/// caching), applies cardinality caps (no silent loss - dropped points are counted on the app and shown in
/// the UI), persists metric/log/error series in batch, and fires <c>app.metric.threshold</c> notifications.
/// </summary>
public sealed class IngestService(
    IAppMonitoringRepository appRepo,
    IAppMetricRepository metricRepo,
    IAppLogRepository logRepo,
    IAppErrorRepository errorRepo,
    IngestKeyHasher hasher,
    IMemoryCache cache,
    INotificationService notificationService,
    AppIngestGate ingestGate,
    TimeProvider timeProvider,
    ILogger<IngestService> logger,
    IAppTelemetryChangePublisher telemetryChanges,
    IngestKeyRejectionLog rejections,
    IHttpContextAccessor? httpContext = null) : IIngestService
{
    // Volume bounds (audit F-MON-06 + S-TECH-VOLM). PG growth is capped on five axes: metric-name
    // cardinality per app (MaxMetricNamesPerApp), points per request (MaxPointsPerRequest), the accepted
    // time window (WithinIngestWindow rejects far-future/expired points that would dodge the retention
    // purge), AttributesJson length (Truncate 1024), and now the SUSTAINED per-app accepted rate
    // (MaxSamplesPerMinutePerApp) so a runaway emitter can't grow AppMetricSample without bound between
    // purges. RESIDUAL (documented, not app-level): distinct attribute-VALUE cardinality is not capped
    // (a high-cardinality attribute such as a request id still yields one row per value until the purge),
    // and physical table-size monitoring is a DB/ops concern (pg_total_relation_size), not enforced here.
    internal const int MaxMetricNamesPerApp = AppMonitoringDefaults.MaximumMetricNamesPerApp;
    internal const int MaxPointsPerRequest = AppMonitoringDefaults.MaximumPointsPerRequest;
    // Sustained ceiling: ~500 points/s per app averaged over a minute. Far above any healthy push cadence,
    // low enough to bound a runaway loop. Applied inside the per-app ingest gate, so it is race-free.
    internal const int MaxSamplesPerMinutePerApp = AppMonitoringDefaults.MaximumSamplesPerMinutePerApp;
    private static readonly TimeSpan KeyCacheTtl = TimeSpan.FromSeconds(60);
    internal const int MaxNegativeKeyCacheEntries = 1024;
    private readonly record struct CachedKeyResolution(int AppId);
    private readonly ConcurrentDictionary<string, byte> negativeKeyCacheEntries = new(StringComparer.Ordinal);
    private readonly object negativeKeyCacheSync = new();
    // Reject emit-sourced timestamps outside a sane window: a future-dated point would fall outside BOTH the
    // aggregation window AND the retention purge, leaving a permanent orphan row (unbounded PG growth) and
    // poisoning the graphs. Drop them honestly (counted). Tolerates modest clock skew + the raw window.
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxPastAge = TimeSpan.FromDays(8);

    private static bool WithinIngestWindow(DateTime ts, DateTime now) =>
        ts <= now + MaxFutureSkew && ts >= now - MaxPastAge;

    public async Task<int?> ResolveAppIdAsync(string ingestKey, CancellationToken ct = default)
    {
        // Every refusal is reported to the deduplicated warning, the request line itself being left
        // to debug by RequestErrorLoggingMiddleware.
        if (string.IsNullOrWhiteSpace(ingestKey))
        {
            rejections.Record(null, RequestOrigin());
            return null;
        }

        var hash = hasher.Hash(ingestKey);
        var cacheKey = KeyCacheKey(hash);
        if (cache.TryGetValue<CachedKeyResolution>(cacheKey, out var cached))
        {
            if (cached.AppId > 0)
                return cached.AppId;
            rejections.Record(hash, RequestOrigin()); // -1 = negative-cached miss
            return null;
        }

        var resolution = await appRepo.ResolveIngestKeyHashAsync(
            hash,
            timeProvider.GetUtcNow().UtcDateTime,
            ct).ConfigureAwait(false);
        // Cache both hits and misses briefly to blunt DB-probing with random keys.
        var cacheDuration = resolution?.ValidUntilUtc is { } validUntil
            ? validUntil - timeProvider.GetUtcNow().UtcDateTime
            : KeyCacheTtl;
        if (cacheDuration > TimeSpan.Zero && (resolution is not null || TryTrackNegativeCacheEntry(cacheKey)))
        {
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = cacheDuration < KeyCacheTtl ? cacheDuration : KeyCacheTtl
            };
            if (resolution is null)
            {
                options.RegisterPostEvictionCallback(
                    static (key, _, _, state) =>
                        ((ConcurrentDictionary<string, byte>)state!).TryRemove((string)key, out _),
                    negativeKeyCacheEntries);
            }
            cache.Set(
                cacheKey,
                new CachedKeyResolution(resolution?.AppId ?? -1),
                options);
        }
        if (resolution is null)
            rejections.Record(hash, RequestOrigin());
        return resolution?.AppId;
    }

    internal const int MaxOriginUserAgentLength = 120;

    /// <summary>Name the sender of the refused request: forwarded client address, user agent and path. The
    /// OTLP body is not read before the key resolves, so the emitter's <c>service.name</c> is not known here.</summary>
    private string? RequestOrigin()
    {
        var request = httpContext?.HttpContext?.Request;
        if (request is null)
            return null;
        var address = httpContext!.HttpContext!.Connection.RemoteIpAddress?.ToString() ?? "unknown-address";
        var agent = request.Headers.UserAgent.ToString();
        if (agent.Length > MaxOriginUserAgentLength)
            agent = agent[..MaxOriginUserAgentLength];
        agent = new string(agent.Where(c => !char.IsControl(c)).ToArray());
        return $"{address} (user-agent \"{agent}\", path {request.Path})";
    }

    internal int TrackedNegativeKeyCount => negativeKeyCacheEntries.Count;

    private bool TryTrackNegativeCacheEntry(string cacheKey)
    {
        lock (negativeKeyCacheSync)
        {
            if (negativeKeyCacheEntries.ContainsKey(cacheKey))
                return true;
            if (negativeKeyCacheEntries.Count >= MaxNegativeKeyCacheEntries)
                return false;
            return negativeKeyCacheEntries.TryAdd(cacheKey, 0);
        }
    }

    private static string KeyCacheKey(string ingestKeyHash) => $"ingest-key:{ingestKeyHash}";

    public void InvalidateKeyCache(string ingestKeyHash)
    {
        if (!string.IsNullOrEmpty(ingestKeyHash))
            cache.Remove(KeyCacheKey(ingestKeyHash));
    }

    public async Task<IngestOutcome> IngestMetricsAsync(int appId, IReadOnlyList<ParsedMetricPoint> points, CancellationToken ct = default)
    {
        // Serialize ingestion per app: the metric-name cardinality budget is read-modify-written against a
        // cached snapshot, so two concurrent ingests for the same app would otherwise race past the cap.
        var gate = ingestGate.For(appId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        (IngestOutcome Outcome, List<object> Alerts) result;
        try
        {
            result = await IngestMetricsCoreAsync(appId, points, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // Record the breach under the application lock, then dispatch alerts after releasing it.
        // Concurrent batches see the recorded breach without waiting for notification delivery.
        foreach (var alert in result.Alerts)
            await notificationService.SendEventAsync(MetricThresholdEvent, alert, ct).ConfigureAwait(false);
        return result.Outcome;
    }

    private const string MetricThresholdEvent = "app.metric.threshold";

    private async Task<(IngestOutcome Outcome, List<object> Alerts)> IngestMetricsCoreAsync(
        int appId, IReadOnlyList<ParsedMetricPoint> points, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (accepted, dropped, capped) = CapByCount(points, MaxPointsPerRequest);

        // Cardinality cap: existing names always pass; new names only while under the per-app ceiling.
        // The name set is cached per app (short TTL) so the hot path avoids a SELECT DISTINCT on every push.
        var (existingNames, nameCount) = await LoadMetricNamesAsync(appId, ct).ConfigureAwait(false);
        var budget = MaxMetricNamesPerApp - nameCount;
        var newNames = new List<string>();

        var samples = new List<AppMetricSample>(accepted.Count);
        // Track per-metric extremes so a threshold breach anywhere in the batch is caught, not only the last point.
        var extremesByMetric = new Dictionary<string, (double Min, double Max)>(StringComparer.Ordinal);
        foreach (var p in accepted)
        {
            var ts = p.Timestamp == default ? now : DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc);
            if (!WithinIngestWindow(ts, now)) { dropped++; continue; }

            if (!existingNames.Contains(p.MetricName))
            {
                if (budget <= 0) { dropped++; continue; }
                existingNames.Add(p.MetricName);
                budget--;
                newNames.Add(p.MetricName);
            }

            samples.Add(new AppMetricSample
            {
                MonitoredAppId = appId,
                Timestamp = ts,
                MetricName = p.MetricName,
                Value = p.Value,
                Unit = Truncate(p.Unit, 32),
                AttributesJson = Truncate(p.AttributesJson, 1024),
                Kind = p.Kind
            });
            extremesByMetric[p.MetricName] = extremesByMetric.TryGetValue(p.MetricName, out var e)
                ? (Math.Min(e.Min, p.Value), Math.Max(e.Max, p.Value))
                : (p.Value, p.Value);
        }

        // Sustained per-app rate cap (S-TECH-VOLM): consume budget only for samples that survived the other
        // caps, and drop the overflow honestly (counted). Race-free: metrics ingestion runs under the gate.
        var granted = ConsumeSampleRateBudget(appId, now, samples.Count);
        if (granted < samples.Count)
        {
            dropped += samples.Count - granted;
            samples.RemoveRange(granted, samples.Count - granted);
        }

        if (samples.Count > 0)
            await metricRepo.AddSamplesAsync(samples, ct).ConfigureAwait(false);
        if (newNames.Count > 0)
        {
            // A name goes to the names table the first time it is seen, which is what the metric
            // explorer and this cache's next refill read, instead of a DISTINCT over the samples.
            await metricRepo.AddMetricNamesAsync(appId, newNames, ct).ConfigureAwait(false);
            cache.Set(MetricNamesCacheKey(appId), existingNames, MetricNamesCacheLifetime);
        }

        await appRepo.TouchIngestAsync(appId, dropped, now, ct).ConfigureAwait(false);
        var alerts = await EvaluateThresholdsAsync(appId, extremesByMetric, now, ct).ConfigureAwait(false);

        if (dropped > 0 || capped)
            logger.LogInformation("Ingest metrics app {AppId}: {Accepted} accepted, {Dropped} dropped (caps)", appId, samples.Count, dropped);
        return (new IngestOutcome(samples.Count, dropped), alerts);
    }

    /// <summary>Cache metric names for one hour and update the set during ingestion. Names added by
    /// another backend can remain unseen until refill, so cross-replica additions can temporarily
    /// exceed the name cap.</summary>
    internal static readonly TimeSpan MetricNamesCacheLifetime = TimeSpan.FromHours(1);

    private static string MetricNamesCacheKey(int appId) => $"metric-names:{appId}";

    private static string RateCacheKey(int appId) => $"ingest-rate:{appId}";

    /// <summary>
    /// Reserves up to <paramref name="requested"/> of the app's remaining accepted-sample budget for the
    /// current one-minute window and returns how many were granted (0..requested). Rolling window keyed by
    /// the minute; best-effort (a cache eviction resets the window early, which only ever loosens the cap).
    /// </summary>
    private int ConsumeSampleRateBudget(int appId, DateTime now, int requested)
    {
        if (requested <= 0) return 0;
        var window = now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        var key = RateCacheKey(appId);
        var used = cache.TryGetValue<(string Window, int Count)>(key, out var entry) && entry.Window == window
            ? entry.Count : 0;
        var grant = Math.Clamp(MaxSamplesPerMinutePerApp - used, 0, requested);
        cache.Set(key, (window, used + grant), TimeSpan.FromMinutes(2));
        return grant;
    }

    /// <summary>Loads the app's distinct metric-name set (cached, 5 min) plus its count. Returns a working
    /// copy the caller may grow; new names are written back to the cache by the caller.</summary>
    private async Task<(HashSet<string> Names, int Count)> LoadMetricNamesAsync(int appId, CancellationToken ct)
    {
        if (cache.TryGetValue<HashSet<string>>(MetricNamesCacheKey(appId), out var cached) && cached is not null)
            return (new HashSet<string>(cached, StringComparer.Ordinal), cached.Count);

        var names = (await metricRepo.GetMetricNamesAsync(appId, ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        cache.Set(MetricNamesCacheKey(appId), new HashSet<string>(names, StringComparer.Ordinal), MetricNamesCacheLifetime);
        return (names, names.Count);
    }

    public async Task<IngestOutcome> IngestLogsAsync(int appId, IReadOnlyList<ParsedLogRecord> records, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (accepted, dropped, _) = CapByCount(records, MaxPointsPerRequest);

        var logs = new List<AppLogEntry>(accepted.Count);
        var loggedErrors = new List<AppErrorUpsert>();
        foreach (var r in accepted)
        {
            var ts = r.Timestamp == default ? now : DateTime.SpecifyKind(r.Timestamp, DateTimeKind.Utc);
            if (!WithinIngestWindow(ts, now)) { dropped++; continue; }
            if (LoggedError(r, ts) is { } loggedError) loggedErrors.Add(loggedError);
            logs.Add(new AppLogEntry
            {
                MonitoredAppId = appId,
                Timestamp = ts,
                SeverityNumber = r.SeverityNumber,
                SeverityText = Truncate(r.SeverityText, 32),
                Body = Truncate(r.Body, 4000) ?? string.Empty,
                AttributesJson = Truncate(r.AttributesJson, 1024)
            });
        }

        if (logs.Count > 0)
            await logRepo.AddLogsAsync(logs, ct).ConfigureAwait(false);
        if (loggedErrors.Count > 0)
            await errorRepo.UpsertErrorsAsync(appId, loggedErrors, ct).ConfigureAwait(false);
        await appRepo.TouchIngestAsync(appId, dropped, now, ct).ConfigureAwait(false);
        // The open Logs tab reloads on this push instead of a Refresh click.
        if (logs.Count > 0)
            await telemetryChanges.PublishAsync(appId, ct).ConfigureAwait(false);
        return new IngestOutcome(logs.Count, dropped);
    }

    /// <summary>OTLP severity number from which a log record is an error (ERROR and FATAL).</summary>
    private const int ErrorSeverityNumber = 17;

    /// <summary>Create or update an error group for error-level logs carrying an exception even
    /// without a span, grouping by exception type and logger.</summary>
    private static AppErrorUpsert? LoggedError(ParsedLogRecord record, DateTime at)
    {
        if (record.SeverityNumber < ErrorSeverityNumber || string.IsNullOrWhiteSpace(record.ExceptionType))
            return null;
        var type = Truncate(record.ExceptionType, 256)!;
        var source = Truncate(record.Source, 512);
        return new AppErrorUpsert(Fingerprint(type, source), type, Truncate(record.Body, 1000) ?? string.Empty, source, at);
    }

    public async Task<IngestOutcome> IngestErrorsAsync(int appId, IReadOnlyList<ParsedError> errors, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (accepted, dropped, _) = CapByCount(errors, MaxPointsPerRequest);

        var upserts = new List<AppErrorUpsert>(accepted.Count);
        foreach (var e in accepted)
        {
            var at = e.Timestamp == default ? now : DateTime.SpecifyKind(e.Timestamp, DateTimeKind.Utc);
            if (!WithinIngestWindow(at, now)) { dropped++; continue; }
            var type = Truncate(e.ExceptionType, 256) ?? "Exception";
            var top = Truncate(e.TopFrame, 512);
            upserts.Add(new AppErrorUpsert(
                Fingerprint(type, top),
                type,
                Truncate(e.Message, 1000) ?? string.Empty,
                top,
                at));
        }

        var groups = upserts.Count > 0 ? await errorRepo.UpsertErrorsAsync(appId, upserts, ct).ConfigureAwait(false) : 0;
        await appRepo.TouchIngestAsync(appId, dropped, now, ct).ConfigureAwait(false);
        // The open Errors tab reloads on this push instead of a Refresh click.
        if (upserts.Count > 0)
            await telemetryChanges.PublishAsync(appId, ct).ConfigureAwait(false);
        return new IngestOutcome(groups, dropped);
    }

    /// <summary> Records the breaches and recoveries of the batch under the app's lock and returns
    /// the alerts to send once it is released. </summary>
    private async Task<List<object>> EvaluateThresholdsAsync(int appId, Dictionary<string, (double Min, double Max)> extremesByMetric, DateTime now, CancellationToken ct)
    {
        var alerts = new List<object>();
        if (extremesByMetric.Count == 0)
            return alerts;

        // One query for all the app's enabled thresholds (was an N+1 per metric name on the hot path).
        var thresholds = await metricRepo.GetEnabledThresholdsForAppAsync(appId, ct).ConfigureAwait(false);
        if (thresholds.Count == 0)
            return alerts;

        var changed = false;
        int? projectId = null;
        foreach (var t in thresholds)
        {
            if (!extremesByMetric.TryGetValue(t.MetricName, out var extreme))
                continue; // this metric wasn't in the batch

            // Evaluate against the batch extreme relevant to the operator, so a breach anywhere in the
            // batch fires - not only when the last point happens to breach (audit finding, Élevé).
            var value = t.Operator is ComparisonOperator.GreaterThan or ComparisonOperator.GreaterThanOrEqual
                ? extreme.Max : extreme.Min;
            var breached = Breaches(value, t.Operator, t.Threshold);

            if (breached && !t.IsBreached)
            {
                t.IsBreached = true;
                t.LastTriggeredAt = now;
                changed = true;
                // The owning project, read once and only when a breach fires: the AI tasks and the
                // per-user notifications of an event are those of the project it is about.
                projectId ??= await appRepo.GetAppProjectIdAsync(appId, ct).ConfigureAwait(false);
                alerts.Add(new
                {
                    AppId = appId,
                    ProjectId = projectId,
                    t.MetricName,
                    Operator = t.Operator.ToString(),
                    t.Threshold,
                    Value = value,
                    At = now
                });
            }
            else if (!breached && t.IsBreached)
            {
                t.IsBreached = false;
                changed = true;
            }
        }

        if (changed)
            await metricRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        return alerts;
    }

    private static bool Breaches(double value, ComparisonOperator op, double threshold) => op switch
    {
        ComparisonOperator.GreaterThan => value > threshold,
        ComparisonOperator.GreaterThanOrEqual => value >= threshold,
        ComparisonOperator.LessThan => value < threshold,
        ComparisonOperator.LessThanOrEqual => value <= threshold,
        _ => false
    };

    private static (List<T> Accepted, int Dropped, bool Capped) CapByCount<T>(IReadOnlyList<T> items, int max)
    {
        if (items.Count <= max)
            return (items.ToList(), 0, false);
        return (items.Take(max).ToList(), items.Count - max, true);
    }

    private static string Fingerprint(string type, string? topFrame)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{type}|{topFrame}"));
        return Convert.ToHexString(bytes)[..32];
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
