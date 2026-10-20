// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Portsentry;

public class PortsentryRepository(AppDbContext db) : IPortsentryRepository
{
    public async Task<PortsentryState?> GetStateAsync(int serverId, CancellationToken ct = default)
    {
        return await db.PortsentryStates
            .AsNoTracking()
            .Where(p => p.ServerId == serverId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<PortsentryBlockedIp>> GetBlockedIpsAsync(int serverId, CancellationToken ct = default)
    {
        return await db.PortsentryBlockedIps
            .AsNoTracking()
            .Where(b => b.ServerId == serverId)
            .OrderByDescending(b => b.BlockedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<PortsentryWhitelistIp>> GetWhitelistAsync(int serverId, CancellationToken ct = default)
    {
        return await db.PortsentryWhitelistIps
            .AsNoTracking()
            .Where(w => w.ServerId == serverId)
            .OrderBy(w => w.IpAddress)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<PortsentryBlockedIp> Items, int Total)> GetBlockedIpsPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = db.PortsentryBlockedIps.AsNoTracking().Where(item => item.ServerId == serverId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(item =>
                EF.Functions.ILike(item.IpAddress, pattern) ||
                EF.Functions.ILike(item.Protocol, pattern) ||
                EF.Functions.ILike(item.Reason, pattern));
        }

        // The header filters, after the server scope and before the count.
        query = PortsentryListQuery.BlockedColumns.ApplyFilters(query, filters);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("ipaddress", false) => query.OrderBy(item => item.IpAddress).ThenBy(item => item.Id),
            ("ipaddress", true) => query.OrderByDescending(item => item.IpAddress).ThenBy(item => item.Id),
            ("protocol", false) => query.OrderBy(item => item.Protocol).ThenByDescending(item => item.BlockedAt).ThenBy(item => item.Id),
            ("protocol", true) => query.OrderByDescending(item => item.Protocol).ThenByDescending(item => item.BlockedAt).ThenBy(item => item.Id),
            ("reason", false) => query.OrderBy(item => item.Reason).ThenByDescending(item => item.BlockedAt).ThenBy(item => item.Id),
            ("reason", true) => query.OrderByDescending(item => item.Reason).ThenByDescending(item => item.BlockedAt).ThenBy(item => item.Id),
            ("blockedat", false) => query.OrderBy(item => item.BlockedAt).ThenBy(item => item.Id),
            _ => query.OrderByDescending(item => item.BlockedAt).ThenBy(item => item.Id)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public async Task<(List<PortsentryWhitelistIp> Items, int Total)> GetWhitelistPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = db.PortsentryWhitelistIps.AsNoTracking().Where(item => item.ServerId == serverId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(item =>
                EF.Functions.ILike(item.IpAddress, pattern) ||
                EF.Functions.ILike(item.Description, pattern));
        }

        // The header filters, after the server scope and before the count.
        query = PortsentryListQuery.WhitelistColumns.ApplyFilters(query, filters);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("description", false) => query.OrderBy(item => item.Description).ThenBy(item => item.IpAddress).ThenBy(item => item.Id),
            ("description", true) => query.OrderByDescending(item => item.Description).ThenBy(item => item.IpAddress).ThenBy(item => item.Id),
            ("createdat", false) => query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id),
            ("createdat", true) => query.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id),
            (_, true) => query.OrderByDescending(item => item.IpAddress).ThenBy(item => item.Id),
            _ => query.OrderBy(item => item.IpAddress).ThenBy(item => item.Id)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public async Task<List<string>> GetBlockedProtocolsAsync(int serverId, CancellationToken ct = default)
    {
        var protocols = await db.PortsentryBlockedIps
            .Where(item => item.ServerId == serverId)
            .Select(item => item.Protocol)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. protocols
            .Where(protocol => !string.IsNullOrWhiteSpace(protocol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<PortsentryWhitelistIp> AddWhitelistIpAsync(PortsentryWhitelistIp entry, CancellationToken ct = default)
    {
        db.PortsentryWhitelistIps.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entry;
    }

    public async Task<bool> RemoveWhitelistIpAsync(int id, int serverId, CancellationToken ct = default)
    {
        var entry = await db.PortsentryWhitelistIps
            .Where(w => w.Id == id && w.ServerId == serverId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (entry is null) return false;
        db.PortsentryWhitelistIps.Remove(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task AddTaskAsync(ServerTask task, CancellationToken ct = default)
    {
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
