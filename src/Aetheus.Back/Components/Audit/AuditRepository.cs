// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Audit;

public class AuditRepository(AppDbContext db) : IAuditRepository
{
    public async Task AddAsync(AuditLog log, CancellationToken ct = default)
    {
        db.AuditLogs.Add(log);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // F-014: lost the cross-instance race on the PreviousHash unique index - detach the
            // failed row so the caller's retry doesn't re-flush it alongside the fresh one.
            db.Entry(log).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<List<AuditLog>> GetPagedAsync(int skip, int take, string? search = null, string? action = null, string? entityType = null, int? entityId = null, DateTime? dateFrom = null, DateTime? dateTo = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = true, IReadOnlyList<GridFilter>? filters = null)
    {
        return await BuildFilteredQuery(search, action, entityType, entityId, dateFrom, dateTo, filters)
            .AsNoTracking()
            .OrderByProperty(sortBy, sortDescending, a => a.Timestamp)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> CountAsync(string? search = null, string? action = null, string? entityType = null, int? entityId = null, DateTime? dateFrom = null, DateTime? dateTo = null, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null)
    {
        return await BuildFilteredQuery(search, action, entityType, entityId, dateFrom, dateTo, filters)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    private IQueryable<AuditLog> BuildFilteredQuery(string? search, string? action, string? entityType, int? entityId, DateTime? dateFrom, DateTime? dateTo,
        IReadOnlyList<GridFilter>? filters)
    {
        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a => a.Username.Contains(search) || (a.Details != null && a.Details.Contains(search)));
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(a => a.EntityType == entityType);
        if (entityId.HasValue)
            query = query.Where(a => a.EntityId == entityId.Value);
        if (dateFrom.HasValue)
            query = query.Where(a => a.Timestamp >= dateFrom.Value);
        if (dateTo.HasValue)
            query = query.Where(a => a.Timestamp <= dateTo.Value);

        // Applies grid header filters alongside the typed query parameters.
        return AuditLogQuery.Columns.ApplyFilters(query, filters);
    }

    public async Task<List<string>> GetDistinctActionsAsync(CancellationToken ct = default)
    {
        return await db.AuditLogs.AsNoTracking()
            .Select(a => a.Action)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<string>> GetDistinctEntityTypesAsync(CancellationToken ct = default)
    {
        return await db.AuditLogs.AsNoTracking()
            .Select(a => a.EntityType)
            .Distinct()
            .OrderBy(e => e)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<string> GetLastHashAsync(CancellationToken ct = default)
    {
        return await db.AuditLogs.AsNoTracking()
            .OrderByDescending(a => a.Id)
            .Select(a => a.Hash)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? string.Empty;
    }

    public IAsyncEnumerable<AuditLog> StreamOrderedAsync()
    {
        // F-006: chain verification streams row-by-row - never materializes the table.
        return db.AuditLogs.AsNoTracking()
            .OrderBy(a => a.Id)
            .AsAsyncEnumerable();
    }

    public async Task<HashSet<string>> GetRecentLoginIpsAsync(string username, int maxEntries, CancellationToken ct = default)
    {
        // F-013: fetch distinct IPs from recent successful login audit entries for this user.
        // Details field stores "IP: <ip>, UA: <ua>" - we extract IPs server-side after query.
        var details = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Username == username && a.Action == "Login" && a.Details != null)
            .OrderByDescending(a => a.Id)
            .Take(maxEntries)
            .Select(a => a.Details!)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var detail in details)
        {
            // Parse "IP: <value>, UA: ..." format
            if (detail.StartsWith("IP: ", StringComparison.Ordinal))
            {
                var commaIdx = detail.IndexOf(',', 4);
                var ip = commaIdx > 4 ? detail[4..commaIdx] : detail[4..];
                ips.Add(ip);
            }
        }

        return ips;
    }
}
