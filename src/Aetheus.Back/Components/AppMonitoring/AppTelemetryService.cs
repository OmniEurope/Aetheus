// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppTelemetryService(
    IAppMonitoringRepository appRepo,
    IAppMetricRepository metricRepo,
    IAppLogRepository logRepo,
    IAppErrorRepository errorRepo,
    IAppVisitorRepository visitorRepo,
    IngestKeyHasher hasher,
    IIngestService ingestService,
    IAuditService audit,
    IConfiguration configuration,
    TimeProvider timeProvider) : IAppTelemetryService
{
    public async Task<IngestKeyResponse?> GenerateIngestKeyAsync(int appId, CancellationToken ct = default)
    {
        var app = await appRepo.GetAppForUpdateAsync(appId, ct).ConfigureAwait(false);
        if (app is null)
            return null;

        var plaintext = hasher.Generate();
        var rotation = IngestKeyRotation.Apply(
            app, plaintext, hasher, configuration, timeProvider);
        await appRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        // Evict every key whose resolution changed (demoted, dropped) so it stops resolving at once, and the
        // new key's hash in case a probe negatively cached it (miss) just before it was assigned - otherwise
        // the new key would 401 for the TTL.
        foreach (var hash in rotation.AffectedHashes)
            ingestService.InvalidateKeyCache(hash);
        await audit.LogAsync(
            rotation.PreviousHash is null ? "CreatedIngestKey" : "RotatedIngestKey",
            "MonitoredApp",
            app.Id,
            $"Version {app.IngestKeyVersion}; previous valid until {app.PreviousIngestKeyValidUntil:O}",
            ct).ConfigureAwait(false);
        return new IngestKeyResponse { Key = plaintext, CreatedAt = rotation.CreatedAt };
    }

    public async Task<bool> RevokeIngestKeyAsync(int appId, CancellationToken ct = default)
    {
        var app = await appRepo.GetAppForUpdateAsync(appId, ct).ConfigureAwait(false);
        if (app is null)
            return false;

        var revokedHash = app.IngestKeyHash;
        var previousHash = app.PreviousIngestKeyHash;
        var secondPreviousHash = app.SecondPreviousIngestKeyHash;
        app.IngestKeyHash = null;
        app.IngestKeyCreatedAt = null;
        app.IngestKeyExpiresAt = null;
        app.PreviousIngestKeyHash = null;
        app.PreviousIngestKeyValidUntil = null;
        app.SecondPreviousIngestKeyHash = null;
        app.SecondPreviousIngestKeyValidUntil = null;
        await appRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        // A revoked key must stop ingesting immediately, not linger for the resolver cache TTL.
        if (revokedHash is not null)
            ingestService.InvalidateKeyCache(revokedHash);
        if (previousHash is not null)
            ingestService.InvalidateKeyCache(previousHash);
        if (secondPreviousHash is not null)
            ingestService.InvalidateKeyCache(secondPreviousHash);
        await audit.LogAsync(
            "RevokedIngestKey",
            "MonitoredApp",
            app.Id,
            $"Version {app.IngestKeyVersion}",
            ct).ConfigureAwait(false);
        return true;
    }

    public Task<List<string>> GetMetricNamesAsync(int appId, CancellationToken ct = default) =>
        metricRepo.GetMetricNamesAsync(appId, ct);

    public Task<List<string?>> GetMetricSeriesGroupsAsync(int appId, string metricName, CancellationToken ct = default) =>
        metricRepo.GetMetricAttributeGroupsAsync(appId, metricName, ct);

    public async Task<MetricSeriesDto> GetMetricSeriesAsync(
        int appId, string metricName, string? attributesJson, int hours, CancellationToken ct = default)
    {
        hours = Math.Clamp(hours, AppMonitoringDefaults.MinimumHistoryHours, AppMonitoringDefaults.MaximumMetricHistoryHours);
        var since = timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours);

        // <= 24h: raw resolution (decimated to a bounded point count server-side). A cumulative counter
        // (Sum) is stored raw and only turned into a per-interval delta here, at read time, never at ingest.
        if (hours <= 24)
        {
            var raw = await metricRepo.GetRawSeriesAsync(appId, metricName, attributesJson, since, ct)
                .ConfigureAwait(false);
            var displayed = raw.Count == 0
                ? []
                : TelemetryMath.ToDisplayValues(raw.Select(s => (s.Timestamp, s.Value)).ToList(), raw[0].Kind);
            return new MetricSeriesDto
            {
                MetricName = metricName,
                Unit = raw.Count > 0 ? raw[^1].Unit : null,
                Points = Decimate(displayed.Select(s => new MetricPointDto { Timestamp = s.Timestamp, Value = s.Value }).ToList())
            };
        }

        // Longer windows: hourly rollup (avg/min/max/p95). The rollup lags - the current incomplete hour and
        // any complete-but-not-yet-swept hours are absent - so append the recent raw tail past the last rolled-up
        // hour, otherwise the most recent points silently vanish from 7d/90d charts.
        var hourly = await metricRepo.GetHourlySeriesAsync(appId, metricName, attributesJson, since, ct)
            .ConfigureAwait(false);
        var points = hourly.Select(h => new MetricPointDto
        {
            Timestamp = h.HourUtc,
            Value = h.AvgValue,
            Min = h.MinValue,
            Max = h.MaxValue,
            P95 = h.P95Value
        }).ToList();

        var tailSince = points.Count > 0 ? points[^1].Timestamp.AddHours(1) : since;
        var tailRaw = await metricRepo.GetRawSeriesAsync(appId, metricName, attributesJson, tailSince, ct)
            .ConfigureAwait(false);
        var tailDisplayed = tailRaw.Count == 0
            ? []
            : TelemetryMath.ToDisplayValues(tailRaw.Select(s => (s.Timestamp, s.Value)).ToList(), tailRaw[0].Kind);
        points.AddRange(tailDisplayed.Select(s => new MetricPointDto { Timestamp = s.Timestamp, Value = s.Value }));

        return new MetricSeriesDto
        {
            MetricName = metricName,
            Unit = tailRaw.Count > 0 ? tailRaw[^1].Unit : hourly.Count > 0 ? hourly[^1].Unit : null,
            Points = Decimate(points)
        };
    }

    public async Task<AppPerformanceReportDto> GetPerformanceAsync(int appId, CancellationToken ct = default)
    {
        var samples = await metricRepo.GetLatestExportAsync(
            appId, AppRoutePerformance.MetricNames, AppRoutePerformance.LatestPointLookback, ct).ConfigureAwait(false);
        return AppRoutePerformance.Build(samples);
    }

    public async Task<AppDatabaseSizeReportDto> GetDatabaseSizeAsync(int appId, CancellationToken ct = default)
    {
        var latest = await metricRepo.GetLatestExportAsync(
            appId, AppDatabaseSize.MetricNames, AppDatabaseSize.MeasurementSpread, ct).ConfigureAwait(false);
        if (latest.Count == 0) return AppDatabaseSize.Build(latest, []);
        var dayBefore = latest.Max(sample => sample.Timestamp) - AppDatabaseSize.TrendDistance;
        var earlier = await metricRepo.GetLatestBetweenAsync(
            appId, AppDatabaseSize.MetricNames, dayBefore - AppDatabaseSize.TrendWindow, dayBefore, ct).ConfigureAwait(false);
        return AppDatabaseSize.Build(latest, earlier);
    }

    public async Task<AppVisitorSeriesDto> GetVisitorSeriesAsync(
        int appId, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, AppMonitoringDefaults.MaximumVisitorHistoryDays);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var since = today.AddDays(-(days - 1));
        var counts = await visitorRepo.GetDailyCountsAsync(appId, since, ct).ConfigureAwait(false);
        var byDay = counts.ToDictionary(point => point.DayUtc, point => point.UniqueVisitors);
        var points = Enumerable.Range(0, days)
            .Select(offset => since.AddDays(offset))
            .Select(day => new AppVisitorPointDto
            {
                DayUtc = day,
                UniqueVisitors = byDay.GetValueOrDefault(day)
            })
            .ToList();

        return new AppVisitorSeriesDto
        {
            Today = byDay.GetValueOrDefault(today),
            Points = points
        };
    }

    // Evenly downsample to the shared series limit, preserving chronological order and the last point.
    private static List<MetricPointDto> Decimate(List<MetricPointDto> points)
    {
        if (points.Count <= AppMonitoringDefaults.MaximumSeriesPoints) return points;
        var step = (double)points.Count / AppMonitoringDefaults.MaximumSeriesPoints;
        var result = new List<MetricPointDto>(AppMonitoringDefaults.MaximumSeriesPoints);
        for (var i = 0; i < AppMonitoringDefaults.MaximumSeriesPoints; i++)
            result.Add(points[(int)(i * step)]);
        if (result[^1].Timestamp != points[^1].Timestamp)
            result[^1] = points[^1];
        return result;
    }

    public async Task<List<AppMetricThresholdDto>> GetThresholdsAsync(int appId, CancellationToken ct = default)
    {
        var thresholds = await metricRepo.GetThresholdsForAppAsync(appId, ct).ConfigureAwait(false);
        return thresholds.Select(MapThreshold).ToList();
    }

    public async Task<AppMetricThresholdDto> CreateThresholdAsync(int appId, CreateMetricThresholdRequest request, CancellationToken ct = default)
    {
        var threshold = new AppMetricThreshold
        {
            MonitoredAppId = appId,
            MetricName = request.MetricName.Trim(),
            Operator = request.Operator,
            Threshold = request.Threshold,
            Enabled = request.Enabled
        };
        await metricRepo.AddThresholdAsync(threshold, ct).ConfigureAwait(false);
        return MapThreshold(threshold);
    }

    public Task<int?> GetThresholdAppIdAsync(int thresholdId, CancellationToken ct = default) =>
        metricRepo.GetThresholdAppIdAsync(thresholdId, ct);

    public async Task<bool> DeleteThresholdAsync(int thresholdId, CancellationToken ct = default)
    {
        var threshold = await metricRepo.GetThresholdForUpdateAsync(thresholdId, ct).ConfigureAwait(false);
        if (threshold is null)
            return false;
        await metricRepo.RemoveThresholdAsync(threshold, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<PaginatedResult<AppLogEntryDto>> GetLogsAsync(
        int appId, int hours, int? minSeverity, string? search, int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null, string? sortBy = null, bool sortDescending = true)
    {
        page = Math.Max(1, page);
        pageSize = PaginationDefaults.Clamp(pageSize);
        var since = timeProvider.GetUtcNow().UtcDateTime.AddHours(-Math.Clamp(hours, AppMonitoringDefaults.MinimumHistoryHours, AppMonitoringDefaults.MaximumHistoryHours));

        var (items, total) = await logRepo.GetLogsAsync(appId, since, minSeverity, search, page, pageSize, ct, filters, sortBy, sortDescending).ConfigureAwait(false);
        return new PaginatedResult<AppLogEntryDto>
        {
            Items = items.Select(l => new AppLogEntryDto
            {
                Id = l.Id,
                Timestamp = l.Timestamp,
                SeverityNumber = l.SeverityNumber,
                SeverityText = l.SeverityText,
                Body = l.Body,
                AttributesJson = l.AttributesJson
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<List<string>> GetErrorExceptionTypesAsync(int appId, CancellationToken ct = default) =>
        errorRepo.GetExceptionTypesAsync(appId, AppErrorQuery.MaximumExceptionTypes, ct);

    public async Task<PaginatedResult<AppErrorEventDto>> GetErrorsAsync(
        int appId, int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null, string? sortBy = null, bool sortDescending = true)
    {
        page = Math.Max(1, page);
        pageSize = PaginationDefaults.Clamp(pageSize);
        var (items, total) = await errorRepo.GetErrorsAsync(appId, page, pageSize, ct, filters, sortBy, sortDescending).ConfigureAwait(false);
        return new PaginatedResult<AppErrorEventDto>
        {
            Items = items.Select(e => new AppErrorEventDto
            {
                Id = e.Id,
                Fingerprint = e.Fingerprint,
                ExceptionType = e.ExceptionType,
                Message = e.Message,
                TopFrame = e.TopFrame,
                OccurrenceCount = e.OccurrenceCount,
                FirstSeenAt = e.FirstSeenAt,
                LastSeenAt = e.LastSeenAt
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    private static AppMetricThresholdDto MapThreshold(AppMetricThreshold t) => new()
    {
        Id = t.Id,
        MonitoredAppId = t.MonitoredAppId,
        MetricName = t.MetricName,
        Operator = t.Operator,
        Threshold = t.Threshold,
        Enabled = t.Enabled,
        IsBreached = t.IsBreached,
        LastTriggeredAt = t.LastTriggeredAt
    };
}
