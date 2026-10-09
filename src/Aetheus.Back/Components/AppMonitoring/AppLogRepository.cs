// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppLogRepository(AppDbContext db) : IAppLogRepository
{
    public async Task AddLogsAsync(IEnumerable<AppLogEntry> logs, CancellationToken ct = default)
    {
        db.AppLogEntries.AddRange(logs);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<AppLogEntry> Items, int TotalCount)> GetLogsAsync(
        int appId, DateTime since, int? minSeverity, string? search, int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null, string? sortBy = null, bool sortDescending = true)
    {
        var query = db.AppLogEntries
            .AsNoTracking()
            .Where(l => l.MonitoredAppId == appId && l.Timestamp >= since);

        if (minSeverity is { } min)
            query = query.Where(l => l.SeverityNumber >= min);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(l => l.Body.Contains(search));
        // Recette R-358: the grid's header filters narrow the whole log before the count.
        query = AppLogQuery.Filters.ApplyFilters(query, filters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        // The requested column first, then newest first and the id, so pages never overlap.
        var ordered = AppLogQuery.Sorts.ApplySorts(query, AppLogQuery.SortsOf(sortBy, sortDescending)) is { } sorted
            ? sorted.ThenByDescending(log => log.Timestamp).ThenByDescending(log => log.Id)
            : query.OrderByDescending(log => log.Timestamp).ThenByDescending(log => log.Id);
        var items = await ordered
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, totalCount);
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoff, IReadOnlyCollection<int> appIds, CancellationToken ct = default)
    {
        return db.Database.IsRelational()
            ? await db.AppLogEntries.Where(log => log.Timestamp < cutoff && appIds.Contains(log.MonitoredAppId))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false)
            : await PurgeTrackedAsync(cutoff, appIds, ct).ConfigureAwait(false);
    }

    private async Task<int> PurgeTrackedAsync(DateTime cutoff, IReadOnlyCollection<int> appIds, CancellationToken ct)
    {
        var expired = await db.AppLogEntries.Where(log => log.Timestamp < cutoff && appIds.Contains(log.MonitoredAppId))
            .ToListAsync(ct).ConfigureAwait(false);
        db.RemoveRange(expired);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return expired.Count;
    }
}
