// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.ModuleLinks;

public class ModuleLinkRepository(AppDbContext db) : IModuleLinkRepository
{
    public async Task<List<ModuleLink>> GetLinksAsync(int serverId, CancellationToken ct = default)
    {
        return await db.ModuleLinks
            .AsNoTracking()
            .Where(l => l.ServerId == serverId)
            .OrderBy(l => l.SourceType).ThenBy(l => l.SourceIdentifier)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ModuleLink>> GetLinksForResourceAsync(int serverId, ModuleLinkType sourceType, string sourceIdentifier, CancellationToken ct = default)
    {
        return await db.ModuleLinks
            .AsNoTracking()
            .Where(l => l.ServerId == serverId &&
                   ((l.SourceType == sourceType && l.SourceIdentifier == sourceIdentifier) ||
                    (l.TargetType == sourceType && l.TargetIdentifier == sourceIdentifier)))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<ModuleLink> Items, int TotalCount)> GetLinksPageAsync(
        int serverId, ModuleLinkPageRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var identifiers = request.ResourceIdentifiers
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (identifiers.Count == 0) return ([], 0);

        var query = db.ModuleLinks
            .AsNoTracking()
            .Where(link => link.ServerId == serverId
                && ((link.SourceType == request.SourceType && identifiers.Contains(link.SourceIdentifier))
                    || (link.TargetType == request.SourceType && identifiers.Contains(link.TargetIdentifier))));

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim().ToLower();
            query = query.Where(link =>
                (link.SourceType == request.SourceType
                    && identifiers.Contains(link.SourceIdentifier)
                    && link.TargetIdentifier.ToLower().Contains(search))
                || (link.TargetType == request.SourceType
                    && identifiers.Contains(link.TargetIdentifier)
                    && link.SourceIdentifier.ToLower().Contains(search)));
        }

        // The header filters, after the server and resource scope and before the count.
        query = ModuleLinkPageQuery.Columns(request.SourceType, identifiers).ApplyFilters(query, request.Filters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var ordered = OrderLinks(query, request, identifiers);
        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, totalCount);
    }

    public async Task<ModuleLink?> FindLinkAsync(int id, CancellationToken ct = default)
    {
        return await db.ModuleLinks
            .FirstOrDefaultAsync(l => l.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(ModuleLink link, CancellationToken ct = default)
    {
        db.ModuleLinks.Add(link);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(ModuleLink link, CancellationToken ct = default)
    {
        db.ModuleLinks.Remove(link);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAutoDetectedAsync(int serverId, CancellationToken ct = default)
    {
        var old = await db.ModuleLinks.Where(l => l.ServerId == serverId && l.IsAutoDetected).ToListAsync(ct).ConfigureAwait(false);
        db.ModuleLinks.RemoveRange(old);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> LinkExistsAsync(int serverId, ModuleLinkType sourceType, string sourceIdentifier, ModuleLinkType targetType, string targetIdentifier, CancellationToken ct = default)
    {
        return await db.ModuleLinks
            .AnyAsync(l => l.ServerId == serverId &&
                     l.SourceType == sourceType && l.SourceIdentifier == sourceIdentifier &&
                     l.TargetType == targetType && l.TargetIdentifier == targetIdentifier, ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ApacheVirtualHost>> GetApacheVhostsAsync(int serverId, CancellationToken ct = default)
    {
        return await db.ApacheVirtualHosts
            .AsNoTracking()
            .Where(v => v.ServerId == serverId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<CertbotCertificate>> GetCertbotCertsAsync(int serverId, CancellationToken ct = default)
    {
        return await db.CertbotCertificates
            .AsNoTracking()
            .Where(c => c.ServerId == serverId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddLinksAsync(List<ModuleLink> links, CancellationToken ct = default)
    {
        db.ModuleLinks.AddRange(links);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static IOrderedQueryable<ModuleLink> OrderLinks(
        IQueryable<ModuleLink> query, ModuleLinkPageRequest request, List<string> identifiers)
    {
        var sortBy = request.SortBy?.Trim().ToLowerInvariant();
        return (sortBy, request.SortDescending) switch
        {
            ("type", true) => query.OrderByDescending(link =>
                link.SourceType == request.SourceType && identifiers.Contains(link.SourceIdentifier)
                    ? link.TargetType : link.SourceType).ThenByDescending(link => link.Id),
            ("type", false) => query.OrderBy(link =>
                link.SourceType == request.SourceType && identifiers.Contains(link.SourceIdentifier)
                    ? link.TargetType : link.SourceType).ThenBy(link => link.Id),
            ("isautodetected", true) => query.OrderByDescending(link => link.IsAutoDetected)
                .ThenByDescending(link => link.Id),
            ("isautodetected", false) => query.OrderBy(link => link.IsAutoDetected).ThenBy(link => link.Id),
            (_, true) => query.OrderByDescending(link =>
                link.SourceType == request.SourceType && identifiers.Contains(link.SourceIdentifier)
                    ? link.TargetIdentifier : link.SourceIdentifier).ThenByDescending(link => link.Id),
            _ => query.OrderBy(link =>
                link.SourceType == request.SourceType && identifiers.Contains(link.SourceIdentifier)
                    ? link.TargetIdentifier : link.SourceIdentifier).ThenBy(link => link.Id)
        };
    }
}
