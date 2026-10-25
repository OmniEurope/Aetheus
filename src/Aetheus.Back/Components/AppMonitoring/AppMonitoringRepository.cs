// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppMonitoringRepository(AppDbContext db) : IAppMonitoringRepository
{
    public async Task<long> GetTelemetryStorageBytesAsync(CancellationToken ct = default)
    {
        if (!db.Database.IsRelational()) return 0;

        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(pg_total_relation_size(quote_ident(schemaname) || '.' || quote_ident(tablename))), 0)::bigint
            FROM pg_tables
            WHERE schemaname = current_schema()
              AND tablename IN ('AppMetricSamples', 'AppMetricHourly', 'AppLogEntries', 'AppErrorEvents', 'AppHealthSamples', 'AppHealthHourly')
            """;
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long bytes ? bytes : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<Dictionary<string, AppHostingServer>> GetVirtualHostServersAsync(
        int projectId,
        IReadOnlyCollection<string> hostNames,
        CancellationToken ct = default)
    {
        if (hostNames.Count == 0) return [];
        var organizationIds = db.Projects.Where(project => project.Id == projectId).Select(project => project.OrganizationId);
        var rows = await db.ApacheVirtualHosts
            .AsNoTracking()
            .Where(vhost => vhost.IsEnabled
                && hostNames.Contains(vhost.ServerName.ToLower())
                && vhost.Server.DeletedAt == null
                && organizationIds.Contains(vhost.Server.OrganizationId))
            .Select(vhost => new { Host = vhost.ServerName.ToLower(), vhost.ServerId, vhost.Server.Name })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .GroupBy(row => row.Host)
            .Where(group => group.Select(row => row.ServerId).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => new AppHostingServer(group.First().ServerId, group.First().Name));
    }

    public async Task<List<MonitoredApp>> GetAppsByProjectAsync(int projectId, CancellationToken ct = default)
    {
        return await db.MonitoredApps
            .AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .Include(a => a.Environment)
            .Include(a => a.Server)
            .OrderBy(a => a.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<MonitoredApp?> GetAppAsync(int id, CancellationToken ct = default)
    {
        return await db.MonitoredApps
            .AsNoTracking()
            .Include(a => a.Environment)
            .Include(a => a.Server)
            .Include(a => a.Project)
            .FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<MonitoredApp?> GetAppForUpdateAsync(int id, CancellationToken ct = default)
    {
        return await db.MonitoredApps
            .FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<int?> GetAppProjectIdAsync(int id, CancellationToken ct = default)
    {
        var projectId = await db.MonitoredApps
            .AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => (int?)a.ProjectId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return projectId;
    }

    public Task<int?> GetServerOrganizationIdAsync(int serverId, CancellationToken ct = default) =>
        db.Servers
            .AsNoTracking()
            .Where(server => server.Id == serverId)
            .Select(server => (int?)server.OrganizationId)
            .FirstOrDefaultAsync(ct);

    public Task<bool> EnvironmentIsUsableByProjectAsync(int environmentId, int projectId, CancellationToken ct = default) =>
        db.Environments
            .AsNoTracking()
            .AnyAsync(environment => environment.Id == environmentId
                && (environment.ProjectId == null || environment.ProjectId == projectId), ct);

    public async Task<Dictionary<int, int>> GetProjectOrgIdsAsync(IReadOnlyCollection<int> projectIds, CancellationToken ct = default)
    {
        return await db.Projects
            .AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.OrganizationId })
            .ToDictionaryAsync(x => x.Id, x => x.OrganizationId, ct).ConfigureAwait(false);
    }

    public async Task<List<MonitoredApp>> GetBackendProbedAppsAsync(CancellationToken ct = default)
    {
        return await db.MonitoredApps
            .AsNoTracking()
            .Where(a => a.Enabled && a.ServerId == null && a.ProbeUrl != null)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<MonitoredApp>> GetAppsForServerAsync(int serverId, CancellationToken ct = default)
    {
        return await db.MonitoredApps
            .AsNoTracking()
            .Where(a => a.Enabled && a.ServerId == serverId && a.ProbeUrl != null)
            .OrderBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<int, int?>> GetAppServerIdsAsync(IReadOnlyCollection<int> appIds, CancellationToken ct = default)
    {
        return await db.MonitoredApps
            .AsNoTracking()
            .Where(a => appIds.Contains(a.Id))
            .Select(a => new { a.Id, a.ServerId })
            .ToDictionaryAsync(x => x.Id, x => x.ServerId, ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<int, MonitoredApp>> GetAppsByIdsForUpdateAsync(IReadOnlyCollection<int> appIds, CancellationToken ct = default)
    {
        return await db.MonitoredApps
            .Where(a => appIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);
    }

    public async Task<List<MonitoredApp>> GetAppsForSummaryAsync(List<int>? accessibleProjectIds, CancellationToken ct = default)
    {
        var query = db.MonitoredApps.AsNoTracking().Where(a => a.Enabled);
        if (accessibleProjectIds is not null)
            query = query.Where(a => accessibleProjectIds.Contains(a.ProjectId));
        return await query
            .Include(a => a.Project)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<int, int>> GetActiveVisitorCountsAsync(
        IReadOnlyCollection<int> appIds,
        DateTime activeAfterUtc,
        CancellationToken ct = default)
    {
        if (appIds.Count == 0)
            return [];

        return await db.AppAnalyticsSessions
            .AsNoTracking()
            .Where(session => appIds.Contains(session.MonitoredAppId)
                && session.LastSeenAtUtc >= activeAfterUtc)
            .GroupBy(session => session.MonitoredAppId)
            .Select(group => new
            {
                AppId = group.Key,
                Count = group.Select(session => session.SessionPseudonym).Distinct().Count()
            })
            .ToDictionaryAsync(item => item.AppId, item => item.Count, ct)
            .ConfigureAwait(false);
    }

    public async Task<List<AppAudiencePeriodTotal>> GetAudienceTotalsAsync(
        IReadOnlyCollection<int> appIds,
        DateOnly todayUtc,
        DateOnly weekStartUtc,
        DateOnly monthStartUtc,
        CancellationToken ct = default)
    {
        if (appIds.Count == 0)
            return [];

        return await db.AppAnalyticsAggregates
            .AsNoTracking()
            .Where(item => appIds.Contains(item.MonitoredAppId)
                && ((item.PeriodKind == AnalyticsPeriodKind.Day && item.PeriodStartUtc == todayUtc)
                    || (item.PeriodKind == AnalyticsPeriodKind.Week && item.PeriodStartUtc == weekStartUtc)
                    || (item.PeriodKind == AnalyticsPeriodKind.Month && item.PeriodStartUtc == monthStartUtc)))
            .GroupBy(item => item.PeriodKind)
            .Select(group => new AppAudiencePeriodTotal(
                group.Key,
                group.Sum(item => item.UniqueVisitors),
                group.Sum(item => item.AuthenticatedUniqueVisitors),
                group.Sum(item => item.Sessions),
                group.Sum(item => item.PageViews)))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddAppAsync(MonitoredApp app, CancellationToken ct = default)
    {
        db.MonitoredApps.Add(app);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAppAsync(MonitoredApp app, CancellationToken ct = default)
    {
        db.MonitoredApps.Remove(app);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddSamplesAsync(IEnumerable<AppHealthSample> samples, CancellationToken ct = default)
    {
        db.AppHealthSamples.AddRange(samples);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<AppHealthSample>> GetSamplesSinceAsync(int appId, DateTime since, CancellationToken ct = default)
    {
        return await db.AppHealthSamples
            .AsNoTracking()
            .Where(s => s.MonitoredAppId == appId && s.Timestamp >= since)
            .OrderBy(s => s.Timestamp)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<(int Up, int Total)> GetRawUptimeAsync(int appId, DateTime since, CancellationToken ct = default)
    {
        var rows = await db.AppHealthSamples
            .AsNoTracking()
            .Where(s => s.MonitoredAppId == appId && s.Timestamp >= since)
            .GroupBy(_ => 1)
            .Select(g => new { Up = g.Count(s => s.IsUp), Total = g.Count() })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return rows is null ? (0, 0) : (rows.Up, rows.Total);
    }

    public async Task<(int Up, int Total)> GetHourlyUptimeAsync(int appId, DateTime since, CancellationToken ct = default)
    {
        var rows = await db.AppHealthHourly
            .AsNoTracking()
            .Where(h => h.MonitoredAppId == appId && h.HourUtc >= since)
            .GroupBy(_ => 1)
            .Select(g => new { Up = g.Sum(h => h.UpCount), Total = g.Sum(h => h.SampleCount) })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return rows is null ? (0, 0) : (rows.Up, rows.Total);
    }

    public async Task<Dictionary<int, AppUptimeWindowCounts>> GetUptimeWindowsAsync(
        IReadOnlyCollection<int> appIds,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        if (appIds.Count == 0)
            return [];

        var since24h = nowUtc.AddHours(-24);
        var since7d = nowUtc.AddDays(-7);
        var since90d = nowUtc.AddDays(-90);
        var currentHourStart = new DateTime(
            nowUtc.Year,
            nowUtc.Month,
            nowUtc.Day,
            nowUtc.Hour,
            0,
            0,
            DateTimeKind.Utc);

        var raw24h = await db.AppHealthSamples
            .AsNoTracking()
            .Where(sample => appIds.Contains(sample.MonitoredAppId) && sample.Timestamp >= since24h)
            .GroupBy(sample => sample.MonitoredAppId)
            .Select(group => new
            {
                AppId = group.Key,
                Up = group.Count(sample => sample.IsUp),
                Total = group.Count(),
                TailUp = group.Count(sample => sample.Timestamp >= currentHourStart && sample.IsUp),
                TailTotal = group.Count(sample => sample.Timestamp >= currentHourStart)
            })
            .ToDictionaryAsync(
                row => row.AppId,
                row => (row.Up, row.Total, row.TailUp, row.TailTotal),
                ct)
            .ConfigureAwait(false);

        var hourly = await db.AppHealthHourly
            .AsNoTracking()
            .Where(bucket => appIds.Contains(bucket.MonitoredAppId) && bucket.HourUtc >= since90d)
            .GroupBy(bucket => bucket.MonitoredAppId)
            .Select(group => new
            {
                AppId = group.Key,
                Up7d = group.Sum(bucket => bucket.HourUtc >= since7d ? bucket.UpCount : 0),
                Total7d = group.Sum(bucket => bucket.HourUtc >= since7d ? bucket.SampleCount : 0),
                Up90d = group.Sum(bucket => bucket.UpCount),
                Total90d = group.Sum(bucket => bucket.SampleCount)
            })
            .ToDictionaryAsync(
                row => row.AppId,
                row => (row.Up7d, row.Total7d, row.Up90d, row.Total90d),
                ct)
            .ConfigureAwait(false);

        var result = new Dictionary<int, AppUptimeWindowCounts>(appIds.Count);
        foreach (var appId in appIds)
        {
            raw24h.TryGetValue(appId, out var raw);
            hourly.TryGetValue(appId, out var rollup);
            result[appId] = new AppUptimeWindowCounts(
                raw.Up,
                raw.Total,
                rollup.Up7d + raw.TailUp,
                rollup.Total7d + raw.TailTotal,
                rollup.Up90d + raw.TailUp,
                rollup.Total90d + raw.TailTotal);
        }

        return result;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<MonitoredApp?> GetDeployTargetAppAsync(int projectId, int? environmentId, CancellationToken ct = default)
    {
        var apps = await db.MonitoredApps
            .Where(a => a.Enabled && a.ProjectId == projectId
                && (environmentId == null || a.EnvironmentId == environmentId || a.EnvironmentId == null))
            .ToListAsync(ct).ConfigureAwait(false);
        // Prefer an exact environment match, then a project-level (null environment) app.
        return apps.FirstOrDefault(a => a.EnvironmentId == environmentId && environmentId != null)
            ?? apps.FirstOrDefault(a => a.EnvironmentId == null)
            ?? (apps.Count == 1 ? apps[0] : null);
    }

    public async Task<IngestKeyResolution?> ResolveIngestKeyHashAsync(
        string ingestKeyHash,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        var match = await db.MonitoredApps
            .AsNoTracking()
            .Where(a => a.Enabled
                        && ((a.IngestKeyHash == ingestKeyHash
                             && a.IngestKeyExpiresAt >= nowUtc)
                            || (a.PreviousIngestKeyHash == ingestKeyHash
                                && a.PreviousIngestKeyValidUntil >= nowUtc)
                            || (a.SecondPreviousIngestKeyHash == ingestKeyHash
                                && a.SecondPreviousIngestKeyValidUntil >= nowUtc)))
            .Select(a => new
            {
                a.Id,
                IsCurrent = a.IngestKeyHash == ingestKeyHash,
                IsPrevious = a.PreviousIngestKeyHash == ingestKeyHash,
                a.PreviousIngestKeyValidUntil,
                a.SecondPreviousIngestKeyValidUntil
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (match is null)
            return null;
        return new IngestKeyResolution(
            match.Id,
            match.IsCurrent ? null
            : match.IsPrevious ? match.PreviousIngestKeyValidUntil
            : match.SecondPreviousIngestKeyValidUntil);
    }

    public async Task<MonitoredApp?> GetAppByAnalyticsSiteIdAsync(
        string siteId,
        CancellationToken ct = default)
    {
        return await db.MonitoredApps.AsNoTracking()
            .FirstOrDefaultAsync(
                app => app.AnalyticsSiteId == siteId,
                ct).ConfigureAwait(false);
    }

    public async Task<List<MonitoredApp>> GetAnalyticsKeyRotationCandidatesAsync(
        DateTime createdBeforeUtc,
        int maxCount,
        CancellationToken ct = default)
    {
        return await db.MonitoredApps.AsNoTracking()
            .Where(app => app.AnalyticsVaultName != null
                          && (app.AnalyticsPendingPseudonymKeyVersion != null
                              || app.AnalyticsPseudonymKeyCreatedAt <= createdBeforeUtc))
            .OrderBy(app => app.Id)
            .Take(Math.Clamp(maxCount, 1, 100))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<MonitoredApp>> GetAnalyticsConfiguredAppsPageAsync(
        int afterId,
        int maxCount,
        CancellationToken ct = default)
    {
        return await db.MonitoredApps.AsNoTracking()
            .Where(app => app.AnalyticsVaultName != null && app.Id > afterId)
            .OrderBy(app => app.Id)
            .Take(Math.Clamp(maxCount, 1, 100))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task TouchIngestAsync(int appId, long droppedDelta, DateTime now, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            await db.MonitoredApps
                .Where(a => a.Id == appId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.LastIngestAt, now)
                    .SetProperty(a => a.IngestDroppedCount, a => a.IngestDroppedCount + droppedDelta), ct)
                .ConfigureAwait(false);
            return;
        }

        // InMemory fallback - ExecuteUpdate not supported
        var app = await db.MonitoredApps.FirstOrDefaultAsync(a => a.Id == appId, ct).ConfigureAwait(false);
        if (app is null) return;
        app.LastIngestAt = now;
        app.IngestDroppedCount += droppedDelta;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> AggregateHourAsync(DateTime hourUtc, CancellationToken ct = default)
    {
        // Process one completed hour per application to bound sample loading.
        var next = hourUtc.AddHours(1);
        var sampledApps = await db.AppHealthSamples
            .AsNoTracking()
            .Where(s => s.Timestamp >= hourUtc && s.Timestamp < next)
            .Select(s => s.MonitoredAppId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (sampledApps.Count == 0)
            return 0;

        var rolledUpApps = await RolledUpAppsAsync(hourUtc, ct).ConfigureAwait(false);

        var toInsert = new List<AppHealthHourly>();
        foreach (var appId in sampledApps.Except(rolledUpApps))
        {
            var samples = await db.AppHealthSamples
                .AsNoTracking()
                .Where(s => s.MonitoredAppId == appId && s.Timestamp >= hourUtc && s.Timestamp < next)
                .Select(s => new { s.IsUp, s.ResponseTimeMs })
                .ToListAsync(ct).ConfigureAwait(false);
            var responseTimes = samples.Where(s => s.ResponseTimeMs.HasValue).Select(s => (double)s.ResponseTimeMs!.Value).ToList();
            toInsert.Add(new AppHealthHourly
            {
                MonitoredAppId = appId,
                HourUtc = hourUtc,
                SampleCount = samples.Count,
                UpCount = samples.Count(s => s.IsUp),
                AvgResponseTimeMs = responseTimes.Count > 0 ? responseTimes.Average() : 0,
                P95ResponseTimeMs = Percentile(responseTimes, 0.95)
            });
        }

        if (toInsert.Count == 0)
            return 0;

        // Concurrent aggregation keeps existing rollups while inserting the remaining applications.
        return await HourlyRollupInsert.AddMissingAsync(db, toInsert, async token =>
        {
            var present = (await RolledUpAppsAsync(hourUtc, token).ConfigureAwait(false)).ToHashSet();
            return row => !present.Contains(row.MonitoredAppId);
        }, ct).ConfigureAwait(false);
    }

    private async Task<List<int>> RolledUpAppsAsync(DateTime hourUtc, CancellationToken ct)
    {
        return await db.AppHealthHourly
            .AsNoTracking()
            .Where(h => h.HourUtc == hourUtc)
            .Select(h => h.MonitoredAppId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> PurgeRawOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            return await db.AppHealthSamples
                .Where(s => s.Timestamp < cutoff)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        // InMemory fallback - ExecuteDelete not supported
        var expired = await db.AppHealthSamples
            .Where(s => s.Timestamp < cutoff)
            .ToListAsync(ct).ConfigureAwait(false);
        db.AppHealthSamples.RemoveRange(expired);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return expired.Count;
    }

    public async Task<int> PurgeHourlyOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            return await db.AppHealthHourly
                .Where(h => h.HourUtc < cutoff)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        // InMemory fallback - ExecuteDelete not supported
        var expired = await db.AppHealthHourly
            .Where(h => h.HourUtc < cutoff)
            .ToListAsync(ct).ConfigureAwait(false);
        db.AppHealthHourly.RemoveRange(expired);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return expired.Count;
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        if (values.Count == 1)
            return values[0];
        var rank = percentile * (values.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high)
            return values[low];
        var weight = rank - low;
        return values[low] * (1 - weight) + values[high] * weight;
    }
}
