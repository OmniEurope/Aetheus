// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Telemetry;

namespace Aetheus.Back.Components.Projects;

public sealed class ProjectStorageRepository(AppDbContext db) : IProjectStorageRepository
{
    public string DatabaseName => db.Database.IsRelational() ? db.Database.GetDbConnection().Database : string.Empty;

    public async Task<IReadOnlyList<ProjectStorageBudget>> GetBudgetsAsync(CancellationToken ct = default)
    {
        var rows = await db.Projects.AsNoTracking()
            .Select(project => new { project.Id, project.Name, project.StorageBudgetMegabytes, project.StorageQuotaAlertLevel })
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(row => new ProjectStorageBudget(
            row.Id, row.Name, ProjectStorageDefaults.BudgetBytes(row.StorageBudgetMegabytes), row.StorageQuotaAlertLevel))];
    }

    public async Task<IReadOnlyList<ProjectTableRows>> CountRowsAsync(CancellationToken ct = default)
    {
        var result = new List<ProjectTableRows>();
        result.AddRange(await CountAsync(ProjectStorageTables.MetricSamples,
            db.AppMetricSamples.AsNoTracking().GroupBy(row => row.MonitoredApp.ProjectId)
                .Select(group => new Count(group.Key, group.LongCount())), ct).ConfigureAwait(false));
        result.AddRange(await CountAsync(ProjectStorageTables.MetricHourly,
            db.AppMetricHourly.AsNoTracking().GroupBy(row => row.MonitoredApp.ProjectId)
                .Select(group => new Count(group.Key, group.LongCount())), ct).ConfigureAwait(false));
        result.AddRange(await CountAsync(ProjectStorageTables.Logs,
            db.AppLogEntries.AsNoTracking().GroupBy(row => row.MonitoredApp.ProjectId)
                .Select(group => new Count(group.Key, group.LongCount())), ct).ConfigureAwait(false));
        result.AddRange(await CountAsync(ProjectStorageTables.AnalysisMetrics,
            db.AnalysisMetrics.AsNoTracking().GroupBy(row => row.ProjectId)
                .Select(group => new Count(group.Key, group.LongCount())), ct).ConfigureAwait(false));
        result.AddRange(await CountAsync(ProjectStorageTables.AnalysisComponents,
            db.AnalysisComponents.AsNoTracking().GroupBy(row => row.ProjectId)
                .Select(group => new Count(group.Key, group.LongCount())), ct).ConfigureAwait(false));
        return result;
    }

    public async Task<IReadOnlyList<int>> GetAppsMeasuringADatabaseAsync(DateTime sinceUtc, CancellationToken ct = default) =>
        await db.AppMetricSamples.AsNoTracking()
            .Where(sample => sample.MetricName == DatabaseSizeMetrics.DatabaseSizeMetric && sample.Timestamp >= sinceUtc)
            .Select(sample => sample.MonitoredAppId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task SetAlertLevelAsync(int projectId, int level, CancellationToken ct = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == projectId, ct).ConfigureAwait(false);
        if (project is null) return;
        project.StorageQuotaAlertLevel = level;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<DateTime?> GetOldestSampleAsync(int projectId, CancellationToken ct = default) =>
        await db.AppMetricSamples.AsNoTracking()
            .Where(sample => sample.MonitoredApp.ProjectId == projectId)
            .MinAsync(sample => (DateTime?)sample.Timestamp, ct).ConfigureAwait(false);

    public async Task<DateTime?> GetOldestLogAsync(int projectId, CancellationToken ct = default) =>
        await db.AppLogEntries.AsNoTracking()
            .Where(log => log.MonitoredApp.ProjectId == projectId)
            .MinAsync(log => (DateTime?)log.Timestamp, ct).ConfigureAwait(false);

    public async Task<int> DeleteOldestSamplesAsync(int projectId, int batchSize, CancellationToken ct = default)
    {
        var ids = await db.AppMetricSamples.AsNoTracking()
            .Where(sample => sample.MonitoredApp.ProjectId == projectId)
            .OrderBy(sample => sample.Timestamp).ThenBy(sample => sample.Id)
            .Select(sample => sample.Id).Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0) return 0;
        if (db.Database.IsRelational())
            return await db.AppMetricSamples.Where(sample => ids.Contains(sample.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var rows = await db.AppMetricSamples.Where(sample => ids.Contains(sample.Id)).ToListAsync(ct).ConfigureAwait(false);
        db.AppMetricSamples.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<int> DeleteOldestLogsAsync(int projectId, int batchSize, CancellationToken ct = default)
    {
        var ids = await db.AppLogEntries.AsNoTracking()
            .Where(log => log.MonitoredApp.ProjectId == projectId)
            .OrderBy(log => log.Timestamp).ThenBy(log => log.Id)
            .Select(log => log.Id).Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0) return 0;
        if (db.Database.IsRelational())
            return await db.AppLogEntries.Where(log => ids.Contains(log.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var rows = await db.AppLogEntries.Where(log => ids.Contains(log.Id)).ToListAsync(ct).ConfigureAwait(false);
        db.AppLogEntries.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    private static async Task<IEnumerable<ProjectTableRows>> CountAsync(
        string table, IQueryable<Count> counts, CancellationToken ct) =>
        (await counts.ToListAsync(ct).ConfigureAwait(false))
            .Select(count => new ProjectTableRows(count.ProjectId, table, count.Rows));

    private sealed record Count(int ProjectId, long Rows);
}
