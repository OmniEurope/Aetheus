// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.ServerApps;

public class ServerAppRepository(AppDbContext db) : IServerAppRepository
{
    public async Task<List<ServerApp>> GetByServerIdAsync(int serverId, CancellationToken ct = default)
    {
        return await db.ServerApps
            .Where(a => a.ServerId == serverId)
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<ServerApp> Items, int TotalCount)> GetPageAsync(
        int serverId, string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null)
    {
        var query = db.ServerApps.Where(app => app.ServerId == serverId).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(app => app.Name.ToLower().Contains(normalized)
                || (app.Version ?? string.Empty).ToLower().Contains(normalized)
                || app.Source.ToLower().Contains(normalized));
        }

        // The header filters, after the server scope and before the count.
        query = ServerAppListQuery.Columns.ApplyFilters(query, filters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy, sortDescending) switch
        {
            ("Version", false) => query.OrderBy(app => app.Version),
            ("Version", true) => query.OrderByDescending(app => app.Version),
            ("Port", false) => query.OrderBy(app => app.Port),
            ("Port", true) => query.OrderByDescending(app => app.Port),
            ("Source", false) => query.OrderBy(app => app.Source),
            ("Source", true) => query.OrderByDescending(app => app.Source),
            ("Status", false) => query.OrderBy(app => app.Status),
            ("Status", true) => query.OrderByDescending(app => app.Status),
            ("Name", true) => query.OrderByDescending(app => app.Name),
            _ => query.OrderBy(app => app.Name)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, totalCount);
    }

    public async Task<List<string>> GetSourcesAsync(int serverId, CancellationToken ct = default)
    {
        var sources = await db.ServerApps
            .Where(app => app.ServerId == serverId)
            .Select(app => app.Source)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. sources
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<ServerApp?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await db.ServerApps.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task<ServerApp> AddAsync(ServerApp app, CancellationToken ct = default)
    {
        db.ServerApps.Add(app);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return app;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(ServerApp app, CancellationToken ct = default)
    {
        db.ServerApps.Remove(app);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
