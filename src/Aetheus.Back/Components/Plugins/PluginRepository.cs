// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Plugins;

public class PluginRepository(AppDbContext db) : IPluginRepository
{
    public async Task<List<PluginRegistration>> GetAllAsync(CancellationToken ct = default)
    {
        return await db.PluginRegistrations
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<List<string>> GetAuthorsAsync(CancellationToken ct = default) =>
        db.PluginRegistrations.AsNoTracking()
            .Where(plugin => plugin.Author != null && plugin.Author != "")
            .Select(plugin => plugin.Author!)
            .Distinct()
            .OrderBy(author => author)
            .ToListAsync(ct);

    public async Task<(List<PluginRegistration> Items, int TotalCount)> GetPageAsync(
        string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? columnFilters = null)
    {
        // The grid's column header filters, before the count.
        var query = PluginListQuery.Columns.ApplyFilters(db.PluginRegistrations.AsNoTracking(), columnFilters);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(plugin =>
                plugin.Name.ToLower().Contains(normalized)
                || plugin.Version.ToLower().Contains(normalized)
                || (plugin.Author ?? string.Empty).ToLower().Contains(normalized));
        }

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy, sortDescending) switch
        {
            ("Version", false) => query.OrderBy(plugin => plugin.Version),
            ("Version", true) => query.OrderByDescending(plugin => plugin.Version),
            ("Author", false) => query.OrderBy(plugin => plugin.Author),
            ("Author", true) => query.OrderByDescending(plugin => plugin.Author),
            ("Type", false) => query.OrderBy(plugin => plugin.Type),
            ("Type", true) => query.OrderByDescending(plugin => plugin.Type),
            ("Status", false) => query.OrderBy(plugin => plugin.Status),
            ("Status", true) => query.OrderByDescending(plugin => plugin.Status),
            ("CreatedAt", false) => query.OrderBy(plugin => plugin.CreatedAt),
            ("CreatedAt", true) => query.OrderByDescending(plugin => plugin.CreatedAt),
            (_, true) => query.OrderByDescending(plugin => plugin.Name),
            _ => query.OrderBy(plugin => plugin.Name)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, totalCount);
    }

    public async Task<PluginRegistration?> FindAsync(int id, CancellationToken ct = default)
    {
        return await db.PluginRegistrations
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<PluginRegistration?> FindByNameVersionAsync(string name, string version, CancellationToken ct = default)
    {
        return await db.PluginRegistrations
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == name && p.Version == version, ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(PluginRegistration plugin, CancellationToken ct = default)
    {
        db.PluginRegistrations.Add(plugin);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(PluginRegistration plugin, CancellationToken ct = default)
    {
        db.PluginRegistrations.Remove(plugin);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
