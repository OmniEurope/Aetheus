// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Components.Environments;

public class EnvironmentRepository(AppDbContext db) : IEnvironmentRepository
{
    public async Task<(List<Environment> Items, int TotalCount)> GetEnvironmentsPagedAsync(
        string? search, int? projectId, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.Environments.AsNoTracking().AsQueryable();

        if (accessibleIds is not null)
            query = query.Where(e => accessibleIds.Contains(e.Id));

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(e => e.Name.Contains(search));

        if (projectId.HasValue)
            query = query.Where(e => e.ProjectId == projectId.Value);

        // The header filters, after the scope and before the count.
        query = EnvironmentListQuery.Columns.ApplyFilters(query, columnFilters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        var items = await query
            .Include(e => e.Project)
            .Include(e => e.Servers).ThenInclude(es => es.Server)
            .OrderByProperty(sortBy, sortDescending, e => e.Name, fallbackDescending: false)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    public async Task<Environment?> GetEnvironmentWithServersAsync(int id, CancellationToken ct = default)
    {
        return await db.Environments
            .AsNoTracking()
            .Include(e => e.Project)
            .Include(e => e.Servers).ThenInclude(es => es.Server)
            .FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<Environment?> FindEnvironmentAsync(int id, CancellationToken ct = default)
    {
        return await db.Environments
            .Include(e => e.Servers)
            .FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<Environment?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        return await db.Environments
            .Include(e => e.Servers).ThenInclude(es => es.Server)
            .FirstOrDefaultAsync(e => e.Name == name, ct).ConfigureAwait(false);
    }

    public async Task AddEnvironmentAsync(Environment environment, CancellationToken ct = default)
    {
        db.Environments.Add(environment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveEnvironmentAsync(Environment environment, CancellationToken ct = default)
    {
        db.Environments.Remove(environment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Environment?> GetEnvironmentForDuplicationAsync(int id, CancellationToken ct = default)
    {
        return await db.Environments
            .AsNoTracking()
            .Where(e => e.Id == id)
            .Include(e => e.Servers)
            .Include(e => e.Checks)
            .Include(e => e.Libraries).ThenInclude(l => l.Entries)
            .Include(e => e.Vaults).ThenInclude(v => v.Secrets)
            .Include(e => e.Pipelines)
            .Include(e => e.LinkedProjectServers)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> NameExistsInProjectAsync(string name, int? projectId, CancellationToken ct = default)
    {
        return await db.Environments
            .AnyAsync(e => e.Name == name && e.ProjectId == projectId, ct).ConfigureAwait(false);
    }

    public async Task<int?> GetProjectServerProjectIdAsync(int projectServerId, CancellationToken ct = default)
    {
        return await db.ProjectServers
            .Where(ps => ps.Id == projectServerId)
            .Select(ps => (int?)ps.ProjectId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> LinkExistsAsync(int envId, int projectServerId, CancellationToken ct = default)
    {
        return await db.Set<EnvironmentProjectServer>()
            .AnyAsync(l => l.EnvironmentId == envId && l.ProjectServerId == projectServerId, ct).ConfigureAwait(false);
    }

    public async Task AddLinkAsync(EnvironmentProjectServer link, CancellationToken ct = default)
    {
        db.Set<EnvironmentProjectServer>().Add(link);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveLinkAsync(int envId, int projectServerId, CancellationToken ct = default)
    {
        var link = await db.Set<EnvironmentProjectServer>()
            .FirstOrDefaultAsync(l => l.EnvironmentId == envId && l.ProjectServerId == projectServerId, ct).ConfigureAwait(false);
        if (link is not null)
        {
            db.Set<EnvironmentProjectServer>().Remove(link);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
