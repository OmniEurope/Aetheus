// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AgentPools;

public class AgentPoolRepository(AppDbContext db) : IAgentPoolRepository
{
    public async Task<(List<AgentPool> Items, int TotalCount)> GetPoolsPagedAsync(
        string? search, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = db.AgentPools.AsNoTracking().AsQueryable();

        if (accessibleIds is not null)
            query = query.Where(p => accessibleIds.Contains(p.Id));

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search));

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        var items = await query
            .Include(p => p.Servers).ThenInclude(ps => ps.Server)
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    public async Task<AgentPool?> GetPoolWithServersAsync(int id, CancellationToken ct = default)
    {
        return await db.AgentPools
            .AsNoTracking()
            .Include(p => p.Servers).ThenInclude(ps => ps.Server)
            .FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<AgentPool?> FindPoolAsync(int id, CancellationToken ct = default)
    {
        return await db.AgentPools
            .Include(p => p.Servers)
            .FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<AgentPool?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        return await db.AgentPools
            .AsNoTracking()
            .Include(p => p.Servers).ThenInclude(ps => ps.Server)
            .FirstOrDefaultAsync(p => p.Name == name, ct).ConfigureAwait(false);
    }

    public async Task<bool> NameTakenAsync(string name, int? exceptPoolId, CancellationToken ct = default)
    {
        return await db.AgentPools
            .AsNoTracking()
            .AnyAsync(p => p.Name == name && (exceptPoolId == null || p.Id != exceptPoolId), ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> AllServersInOrganizationAsync(
        int organizationId, IReadOnlyCollection<int> serverIds, CancellationToken ct = default)
    {
        var wanted = serverIds.Distinct().ToList();
        if (wanted.Count == 0) return true;
        var owned = await db.Servers
            .AsNoTracking()
            .CountAsync(s => wanted.Contains(s.Id) && s.OrganizationId == organizationId, ct)
            .ConfigureAwait(false);
        return owned == wanted.Count;
    }

    public async Task<int?> GetDefaultOrganizationIdAsync(CancellationToken ct = default) =>
        await db.Organizations
            .AsNoTracking()
            .Where(o => o.Slug == "aetheus")
            .Select(o => (int?)o.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task AddPoolAsync(AgentPool pool, CancellationToken ct = default)
    {
        db.AgentPools.Add(pool);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemovePoolAsync(AgentPool pool, CancellationToken ct = default)
    {
        db.AgentPools.Remove(pool);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
