// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PackageFeeds;

public class PackageFeedRepository(AppDbContext db) : IPackageFeedRepository
{
    /// <summary>The header filters of the package feeds admin grid.</summary>
    internal static readonly GridQueryMap<PackageFeed> Columns = new GridQueryMap<PackageFeed>()
        .Text("Name", f => f.Name)
        .Enum("FeedType", f => f.FeedType)
        .Text("UpstreamUrl", f => f.UpstreamUrl)
        .Number("PackageCount", f => f.Packages.Count);

    public async Task<(List<PackageFeedDto> Items, int Total)> GetPagedFeedsAsync(
        int? projectId, string? search, int page, int pageSize,
        string? sortBy, bool sortDescending, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.PackageFeeds
            .AsNoTracking()
            .AsQueryable();

        if (projectId.HasValue)
            query = query.Where(f => f.ProjectId == projectId.Value || f.ProjectId == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(f => EF.Functions.ILike(f.Name, pattern)
                || EF.Functions.ILike(f.UpstreamUrl, pattern)
                || (f.Description != null && EF.Functions.ILike(f.Description, pattern)));
        }

        query = Columns.ApplyFilters(query, columnFilters);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = ApplySort(query, sortBy, sortDescending);

        // Project server-side: the package count becomes a COUNT subquery instead of loading every
        // Package entity for a list view (audit perf). Project/ServiceConnection names are joined, not
        // Include-materialized.
        var items = await query
            .Select(f => new PackageFeedDto
            {
                Id = f.Id,
                Name = f.Name,
                Description = f.Description,
                FeedType = f.FeedType,
                UpstreamUrl = f.UpstreamUrl,
                ProjectId = f.ProjectId,
                ProjectName = f.Project != null ? f.Project.Name : null,
                ServiceConnectionName = f.ServiceConnection != null ? f.ServiceConnection.Name : null,
                PackageCount = f.Packages.Count,
                CreatedAt = f.CreatedAt
            })
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, total);
    }

    internal static IQueryable<PackageFeed> ApplySort(
        IQueryable<PackageFeed> query, string? sortBy, bool descending) =>
        (sortBy?.Trim().ToLowerInvariant(), descending) switch
        {
            ("feedtype", false) => query.OrderBy(f => f.FeedType).ThenBy(f => f.Name).ThenBy(f => f.Id),
            ("feedtype", true) => query.OrderByDescending(f => f.FeedType).ThenBy(f => f.Name).ThenBy(f => f.Id),
            ("upstreamurl", false) => query.OrderBy(f => f.UpstreamUrl).ThenBy(f => f.Id),
            ("upstreamurl", true) => query.OrderByDescending(f => f.UpstreamUrl).ThenBy(f => f.Id),
            ("packagecount", false) => query.OrderBy(f => f.Packages.Count).ThenBy(f => f.Name).ThenBy(f => f.Id),
            ("packagecount", true) => query.OrderByDescending(f => f.Packages.Count).ThenBy(f => f.Name).ThenBy(f => f.Id),
            ("createdat", false) => query.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id),
            ("createdat", true) => query.OrderByDescending(f => f.CreatedAt).ThenBy(f => f.Id),
            (_, true) => query.OrderByDescending(f => f.Name).ThenBy(f => f.Id),
            _ => query.OrderBy(f => f.Name).ThenBy(f => f.Id)
        };

    public async Task<PackageFeed?> GetFeedDetailAsync(int id, CancellationToken ct = default)
    {
        return await db.PackageFeeds
            .AsNoTracking()
            .Where(f => f.Id == id)
            .Include(f => f.Project)
            .Include(f => f.ServiceConnection)
            .Include(f => f.Packages.OrderBy(p => p.Name))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<PackageFeed?> FindFeedAsync(int id, CancellationToken ct = default)
    {
        return await db.PackageFeeds.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task AddFeedAsync(PackageFeed feed, CancellationToken ct = default)
    {
        await db.PackageFeeds.AddAsync(feed, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveFeedAsync(PackageFeed feed, CancellationToken ct = default)
    {
        db.PackageFeeds.Remove(feed);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PackageEntry?> FindPackageByNameAsync(int feedId, string name, CancellationToken ct = default)
    {
        return await db.PackageEntries
            .Where(p => p.PackageFeedId == feedId && p.Name == name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<PackageEntry?> FindPackageAsync(int packageId, CancellationToken ct = default)
    {
        return await db.PackageEntries.FindAsync([packageId], ct).ConfigureAwait(false);
    }

    // Tracked (not AsNoTracking): the sync engine mutates these entries in place.
    public async Task<List<PackageEntry>> GetTrackedPackagesAsync(int feedId, CancellationToken ct = default)
    {
        return await db.PackageEntries
            .Where(p => p.PackageFeedId == feedId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<int>> GetFeedIdsAsync(CancellationToken ct = default)
    {
        return await db.PackageFeeds.AsNoTracking().Select(f => f.Id).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddPackageAsync(PackageEntry entry, CancellationToken ct = default)
    {
        await db.PackageEntries.AddAsync(entry, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemovePackageAsync(PackageEntry entry, CancellationToken ct = default)
    {
        db.PackageEntries.Remove(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
