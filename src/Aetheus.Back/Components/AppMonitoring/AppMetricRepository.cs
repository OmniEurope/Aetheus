// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppMetricRepository(AppDbContext db) : IAppMetricRepository
{
    public async Task AddSamplesAsync(IEnumerable<AppMetricSample> samples, CancellationToken ct = default)
    {
        db.AppMetricSamples.AddRange(samples);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Recette R-477: read from the names table, one row per name, not from the samples.</summary>
    public async Task<List<string>> GetMetricNamesAsync(int appId, CancellationToken ct = default)
    {
        return await db.AppMetricNames
            .AsNoTracking()
            .Where(n => n.MonitoredAppId == appId)
            .Select(n => n.Name)
            .OrderBy(n => n)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddMetricNamesAsync(int appId, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        if (names.Count == 0) return;
        var known = await db.AppMetricNames.AsNoTracking()
            .Where(n => n.MonitoredAppId == appId && names.Contains(n.Name))
            .Select(n => n.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        var added = names.Except(known, StringComparer.Ordinal)
            .Select(name => new AppMetricName { MonitoredAppId = appId, Name = name })
            .ToList();
        if (added.Count == 0) return;

        db.AppMetricNames.AddRange(added);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (AppErrorRepository.IsUniqueViolation(ex))
        {
            // Mandatory catch: the other blue-green colour recorded one of these names between the read
            // and the write. The name is there, which is all this call is for; the rows that did not go
            // in are dropped from the context so a later save does not try them again, and the next
            // batch records whichever name is still missing.
            foreach (var name in added)
                db.Entry(name).State = EntityState.Detached;
        }
    }

    public async Task<int> PruneMetricNamesAsync(CancellationToken ct = default)
    {
        var unused = db.AppMetricNames.Where(name => !db.AppMetricSamples.Any(sample =>
            sample.MonitoredAppId == name.MonitoredAppId && sample.MetricName == name.Name));
        if (db.Database.IsRelational())
            return await unused.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var rows = await unused.ToListAsync(ct).ConfigureAwait(false);
        db.AppMetricNames.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<int> CountDistinctMetricNamesAsync(int appId, CancellationToken ct = default)
    {
        return await db.AppMetricNames
            .AsNoTracking()
            .CountAsync(n => n.MonitoredAppId == appId, ct).ConfigureAwait(false);
    }

    public async Task<List<AppMetricSample>> GetRawSeriesAsync(
        int appId, string metricName, string? attributesJson, DateTime since, CancellationToken ct = default)
    {
        return await db.AppMetricSamples
            .AsNoTracking()
            .Where(m => m.MonitoredAppId == appId
                        && m.MetricName == metricName
                        && m.AttributesJson == attributesJson
                        && m.Timestamp >= since)
            .OrderBy(m => m.Timestamp)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<AppMetricHourly>> GetHourlySeriesAsync(
        int appId, string metricName, string? attributesJson, DateTime since, CancellationToken ct = default)
    {
        return await db.AppMetricHourly
            .AsNoTracking()
            .Where(m => m.MonitoredAppId == appId
                        && m.MetricName == metricName
                        && m.AttributesJson == attributesJson
                        && m.HourUtc >= since)
            .OrderBy(m => m.HourUtc)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The attribute groups of one metric, the most recently reported first. The chart selects the
    /// first group by default, and the order used to be alphabetical: the <c>null</c> group left by
    /// samples stored before attributes were kept sorted first, had nothing in the last 24 hours, and
    /// every multi-group metric opened on "not enough data points" while its live series were
    /// arriving (measured on production, 2026-09-12: 27 of 44 metrics opened empty, all 44 had data).
    /// </summary>
    public async Task<List<string?>> GetMetricAttributeGroupsAsync(
        int appId, string metricName, CancellationToken ct = default)
    {
        var raw = await db.AppMetricSamples.AsNoTracking()
            .Where(m => m.MonitoredAppId == appId && m.MetricName == metricName)
            .GroupBy(m => m.AttributesJson)
            .Select(g => new { Attributes = g.Key, Last = g.Max(m => m.Timestamp) })
            .ToListAsync(ct).ConfigureAwait(false);
        var hourly = await db.AppMetricHourly.AsNoTracking()
            .Where(m => m.MonitoredAppId == appId && m.MetricName == metricName)
            .GroupBy(m => m.AttributesJson)
            .Select(g => new { Attributes = g.Key, Last = g.Max(m => m.HourUtc) })
            .ToListAsync(ct).ConfigureAwait(false);
        return raw.Concat(hourly)
            .GroupBy(group => group.Attributes)
            .Select(group => (Attributes: group.Key, Last: group.Max(item => item.Last)))
            .OrderByDescending(group => group.Last)
            .ThenBy(group => group.Attributes, StringComparer.Ordinal)
            .Take(AppMonitoringDefaults.MaximumMetricAttributeGroups)
            .Select(group => group.Attributes)
            .ToList();
    }

    public async Task<List<AppMetricSample>> GetLatestExportAsync(
        int appId, IReadOnlyCollection<string> metricNames, TimeSpan lookback, CancellationToken ct = default)
    {
        var newest = await db.AppMetricSamples.AsNoTracking()
            .Where(m => m.MonitoredAppId == appId && metricNames.Contains(m.MetricName))
            .MaxAsync(m => (DateTime?)m.Timestamp, ct).ConfigureAwait(false);
        if (newest is null) return [];

        // Recette R2-082: a route is exported only for a minute in which it was called, so its newest
        // point can be hours older than the newest export.
        return await GetLatestPerSeriesAsync(appId, metricNames, newest.Value - lookback, newest.Value, ct)
            .ConfigureAwait(false);
    }

    public async Task<List<DateTime>> GetRecentSampleInstantsAsync(
        int appId, DateTime since, int take, CancellationToken ct = default)
    {
        return await db.AppMetricSamples.AsNoTracking()
            .Where(m => m.MonitoredAppId == appId && m.Timestamp >= since)
            .Select(m => m.Timestamp)
            .Distinct()
            .OrderByDescending(timestamp => timestamp)
            .Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<List<AppMetricSample>> GetLatestBetweenAsync(
        int appId, IReadOnlyCollection<string> metricNames, DateTime floor, DateTime ceiling,
        CancellationToken ct = default) =>
        GetLatestPerSeriesAsync(appId, metricNames, floor, ceiling, ct);

    /// <summary>The newest point of each series within [<paramref name="floor"/>, <paramref name="ceiling"/>].
    /// The newest instant of each series is found in the database (one row per series), then only the
    /// samples written at those instants are read: memory follows the number of series, not the number
    /// of minutes in the window.</summary>
    private async Task<List<AppMetricSample>> GetLatestPerSeriesAsync(
        int appId, IReadOnlyCollection<string> metricNames, DateTime floor, DateTime ceiling, CancellationToken ct)
    {
        var inLookback = db.AppMetricSamples.AsNoTracking()
            .Where(m => m.MonitoredAppId == appId && metricNames.Contains(m.MetricName)
                        && m.Timestamp >= floor && m.Timestamp <= ceiling);
        var lastPoints = await inLookback
            .GroupBy(m => new { m.MetricName, m.AttributesJson })
            .Select(series => new { series.Key.MetricName, series.Key.AttributesJson, Last = series.Max(m => m.Timestamp) })
            .ToListAsync(ct).ConfigureAwait(false);
        var instants = lastPoints.Select(point => point.Last).Distinct().ToList();
        var wanted = lastPoints.Select(point => (point.MetricName, point.AttributesJson, point.Last)).ToHashSet();
        var atInstants = await inLookback
            .Where(m => instants.Contains(m.Timestamp))
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. atInstants
            .Where(m => wanted.Contains((m.MetricName, m.AttributesJson, m.Timestamp)))
            .GroupBy(m => (m.MetricName, m.AttributesJson))
            .Select(series => series.MaxBy(m => m.Id)!)];
    }

    public async Task<List<AppMetricThreshold>> GetThresholdsForAppAsync(int appId, CancellationToken ct = default)
    {
        return await db.AppMetricThresholds
            .AsNoTracking()
            .Where(t => t.MonitoredAppId == appId)
            .OrderBy(t => t.MetricName)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<AppMetricThreshold>> GetEnabledThresholdsForAppAsync(int appId, CancellationToken ct = default)
    {
        // Tracked (not AsNoTracking): the ingest path mutates IsBreached/LastTriggeredAt and saves.
        return await db.AppMetricThresholds
            .Where(t => t.MonitoredAppId == appId && t.Enabled)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<AppMetricThreshold?> GetThresholdForUpdateAsync(int id, CancellationToken ct = default)
    {
        return await db.AppMetricThresholds.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<int?> GetThresholdAppIdAsync(int id, CancellationToken ct = default)
    {
        return await db.AppMetricThresholds
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => (int?)t.MonitoredAppId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task AddThresholdAsync(AppMetricThreshold threshold, CancellationToken ct = default)
    {
        db.AppMetricThresholds.Add(threshold);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveThresholdAsync(AppMetricThreshold threshold, CancellationToken ct = default)
    {
        db.AppMetricThresholds.Remove(threshold);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Rows one purge statement deletes at most: a large backlog is removed in short statements
    /// instead of one that holds its locks and fills the WAL for the whole table.</summary>
    internal const int PurgeBatchSize = 5_000;

    public async Task<int> AggregateHourAsync(DateTime hourUtc, CancellationToken ct = default)
    {
        // R2-020: one completed hour, one application at a time. The sweep used to read every raw sample
        // from the oldest hour still missing rollups up to the last one, for every application at once:
        // the 48-hour startup catch-up raised an OutOfMemoryException in a 1 GB container.
        if (!await MissesRollupsAsync(appId: null, hourUtc, ct).ConfigureAwait(false))
            return 0;

        var next = hourUtc.AddHours(1);
        var appIds = await db.AppMetricSamples
            .AsNoTracking()
            .Where(m => m.Timestamp >= hourUtc && m.Timestamp < next)
            .Select(m => m.MonitoredAppId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var written = 0;
        foreach (var appId in appIds)
        {
            if (await MissesRollupsAsync(appId, hourUtc, ct).ConfigureAwait(false))
                written += await AggregateAppHourAsync(appId, hourUtc, ct).ConfigureAwait(false);
        }
        return written;
    }

    /// <summary>
    /// Whether the hour holds raw series without their hourly rollup, for one application or all of them.
    /// Counted in the database (distinct raw series against rollup rows), so an hour already rolled up
    /// costs two counts instead of a read of all its samples.
    /// </summary>
    private async Task<bool> MissesRollupsAsync(int? appId, DateTime hourUtc, CancellationToken ct)
    {
        var next = hourUtc.AddHours(1);
        var raw = db.AppMetricSamples.AsNoTracking().Where(m => m.Timestamp >= hourUtc && m.Timestamp < next);
        var rolledUp = db.AppMetricHourly.AsNoTracking().Where(h => h.HourUtc == hourUtc);
        if (appId is { } id)
        {
            raw = raw.Where(m => m.MonitoredAppId == id);
            rolledUp = rolledUp.Where(h => h.MonitoredAppId == id);
        }
        var rawSeries = await raw
            .Select(m => new { m.MonitoredAppId, m.MetricName, m.AttributesJson })
            .Distinct()
            .CountAsync(ct).ConfigureAwait(false);
        return rawSeries > 0 && await rolledUp.CountAsync(ct).ConfigureAwait(false) < rawSeries;
    }

    private async Task<int> AggregateAppHourAsync(int appId, DateTime hourUtc, CancellationToken ct)
    {
        // A cumulative counter's first point of the hour needs the last point before it: the read starts
        // one hour earlier. Memory is bounded by two hours of one application's samples.
        var readFrom = hourUtc.AddHours(-1);
        var next = hourUtc.AddHours(1);
        var raw = await db.AppMetricSamples
            .AsNoTracking()
            .Where(m => m.MonitoredAppId == appId && m.Timestamp >= readFrom && m.Timestamp < next)
            .Select(m => new { m.MetricName, m.AttributesJson, m.Unit, m.Kind, m.Timestamp, m.Value })
            .ToListAsync(ct).ConfigureAwait(false);
        var existingKeys = await RolledUpKeysAsync(appId, hourUtc, ct).ConfigureAwait(false);

        var toInsert = new List<AppMetricHourly>();
        // Sum (cumulative counter) series become deltas BEFORE aggregation, across the ordered run that
        // includes the previous hour, so the first point of the hour still gets its delta.
        foreach (var series in raw.GroupBy(m => (m.MetricName, m.AttributesJson)))
        {
            if (existingKeys.Contains(series.Key)) continue;
            var ordered = series.OrderBy(m => m.Timestamp).ToList();
            var kind = ordered[0].Kind;
            var unit = ordered.Select(m => m.Unit).FirstOrDefault(u => u is not null);
            var values = TelemetryMath.ToDisplayValues(ordered.Select(m => (m.Timestamp, m.Value)).ToList(), kind)
                .Where(v => v.Timestamp >= hourUtc)
                .Select(v => v.Value)
                .ToList();
            if (values.Count == 0) continue;
            toInsert.Add(new AppMetricHourly
            {
                MonitoredAppId = appId,
                MetricName = series.Key.MetricName,
                AttributesJson = series.Key.AttributesJson,
                Unit = unit,
                Kind = kind,
                HourUtc = hourUtc,
                SampleCount = values.Count,
                MinValue = values.Min(),
                MaxValue = values.Max(),
                AvgValue = values.Average(),
                P95Value = TelemetryMath.Percentile(values, 0.95)
            });
        }

        if (toInsert.Count == 0) return 0;
        // R-462: a concurrent sweep that wrote some of these series first is not an error.
        return await HourlyRollupInsert.AddMissingAsync(db, toInsert, async token =>
        {
            var present = await RolledUpKeysAsync(appId, hourUtc, token).ConfigureAwait(false);
            return row => !present.Contains((row.MetricName, row.AttributesJson));
        }, ct).ConfigureAwait(false);
    }

    private async Task<HashSet<(string MetricName, string? AttributesJson)>> RolledUpKeysAsync(
        int appId, DateTime hourUtc, CancellationToken ct)
    {
        return (await db.AppMetricHourly
                .AsNoTracking()
                .Where(h => h.MonitoredAppId == appId && h.HourUtc == hourUtc)
                .Select(h => new { h.MetricName, h.AttributesJson })
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(e => (e.MetricName, e.AttributesJson))
            .ToHashSet();
    }

    public async Task<int> PurgeRawOlderThanAsync(DateTime cutoff, IReadOnlyCollection<int> appIds, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            // R-458: in batches of PurgeBatchSize, oldest identifiers first.
            var purged = 0;
            int deleted;
            do
            {
                var batch = db.AppMetricSamples.Where(m => m.Timestamp < cutoff && appIds.Contains(m.MonitoredAppId))
                    .OrderBy(m => m.Id).Select(m => m.Id).Take(PurgeBatchSize);
                deleted = await db.AppMetricSamples.Where(m => batch.Contains(m.Id))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                purged += deleted;
            } while (deleted == PurgeBatchSize);
            return purged;
        }

        var expired = await db.AppMetricSamples.Where(m => m.Timestamp < cutoff && appIds.Contains(m.MonitoredAppId)).ToListAsync(ct).ConfigureAwait(false);
        db.AppMetricSamples.RemoveRange(expired);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return expired.Count;
    }

    public async Task<int> PurgeHourlyOlderThanAsync(DateTime cutoff, IReadOnlyCollection<int> appIds, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            var purged = 0;
            int deleted;
            do
            {
                var batch = db.AppMetricHourly.Where(h => h.HourUtc < cutoff && appIds.Contains(h.MonitoredAppId))
                    .OrderBy(h => h.Id).Select(h => h.Id).Take(PurgeBatchSize);
                deleted = await db.AppMetricHourly.Where(h => batch.Contains(h.Id))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                purged += deleted;
            } while (deleted == PurgeBatchSize);
            return purged;
        }

        var expired = await db.AppMetricHourly.Where(h => h.HourUtc < cutoff && appIds.Contains(h.MonitoredAppId)).ToListAsync(ct).ConfigureAwait(false);
        db.AppMetricHourly.RemoveRange(expired);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return expired.Count;
    }
}
