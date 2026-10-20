// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PackageRegistry;

internal sealed class PackageRegistryRepository(AppDbContext db) : IPackageRegistryRepository
{
    /// <summary> The header filters of the package registry admin grid. The latest version is read
    /// the way the list projects it (newest listed version). </summary>
    internal static readonly GridQueryMap<RegistryPackage> Columns = new GridQueryMap<RegistryPackage>()
        .Enum("Kind", package => package.Kind)
        .Text("Name", package => package.Name)
        .Text("LatestVersion", package => package.Versions
            .Where(version => version.IsListed)
            .OrderByDescending(version => version.CreatedAt)
            .Select(version => version.Version)
            .FirstOrDefault())
        .Number("VersionCount", package => package.Versions.Count)
        .Date("UpdatedAt", package => package.UpdatedAt);

    public async Task<RegistryPackage?> GetPackageAsync(
        PackageRegistryKind kind, string normalizedName, CancellationToken ct = default)
        => await db.RegistryPackages
            .AsNoTracking()
            .Where(package => package.Kind == kind && package.NormalizedName == normalizedName)
            .Include(package => package.Versions)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<PackageRegistryRegistrationPackage?> GetRegistrationPackageAsync(
        PackageRegistryKind kind,
        string normalizedName,
        string? normalizedVersion = null,
        CancellationToken ct = default)
        => await db.RegistryPackages
            .AsNoTracking()
            .Where(package => package.Kind == kind && package.NormalizedName == normalizedName)
            .Select(package => new PackageRegistryRegistrationPackage(
                package.Name,
                package.Description,
                package.Versions
                    .Where(version => normalizedVersion == null
                        || version.NormalizedVersion == normalizedVersion)
                    .Select(version => new PackageRegistryRegistrationVersion(
                        version.Version,
                        version.IsListed,
                        version.CreatedAt,
                        version.Metadata))
                    .ToList()))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<string>?> GetVersionNamesAsync(
        PackageRegistryKind kind, string normalizedName, CancellationToken ct = default)
    {
        var package = await db.RegistryPackages
            .AsNoTracking()
            .Where(item => item.Kind == kind && item.NormalizedName == normalizedName)
            .Select(item => new
            {
                Versions = item.Versions.Select(version => version.Version).ToList()
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return package?.Versions;
    }

    public async Task<RegistryPackageVersion?> GetVersionAsync(
        PackageRegistryKind kind,
        string normalizedName,
        string normalizedVersion,
        CancellationToken ct = default)
        => await db.RegistryPackageVersions
            .AsNoTracking()
            .Where(version => version.RegistryPackage.Kind == kind
                && version.RegistryPackage.NormalizedName == normalizedName
                && version.NormalizedVersion == normalizedVersion)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<RegistryPackage?> GetPackageForUpdateAsync(
        PackageRegistryKind kind, string normalizedName, CancellationToken ct = default)
        => await db.RegistryPackages
            .Where(package => package.Kind == kind && package.NormalizedName == normalizedName)
            .Include(package => package.Versions)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<RegistryPackage?> GetPackageWithFilesAsync(
        PackageRegistryKind kind, string normalizedName, bool forUpdate, CancellationToken ct = default)
    {
        var query = db.RegistryPackages
            .Where(package => package.Kind == kind && package.NormalizedName == normalizedName)
            .Include(package => package.Versions)
            .ThenInclude(version => version.Files)
            .AsSplitQuery();
        if (!forUpdate)
            query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<RegistryPackage?> GetPackageByIdAsync(int id, CancellationToken ct = default)
        => await db.RegistryPackages
            .AsNoTracking()
            .Where(package => package.Id == id)
            .Include(package => package.Versions)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<RegistryPackage?> GetPackageByIdForUpdateAsync(int id, CancellationToken ct = default)
        => await db.RegistryPackages
            .Where(package => package.Id == id)
            .Include(package => package.Versions)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<(List<PackageRegistrySearchPackage> Items, int Total)> SearchAsync(
        PackageRegistryKind kind, string? search, int skip, int take, bool includePrerelease,
        CancellationToken ct = default)
    {
        var query = db.RegistryPackages
            .AsNoTracking()
            .Where(package => package.Kind == kind
                && package.Versions.Any(version => version.IsListed
                    && (includePrerelease || !version.IsPrerelease)));

        query = ApplySearch(query, search);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderBy(package => package.Name)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200))
            .Select(package => new PackageRegistrySearchPackage(
                package.Name,
                package.Description,
                package.DistTagsJson,
                package.Versions.Select(version => new PackageRegistrySearchVersion(
                    version.Id,
                    version.Version,
                    version.NormalizedVersion,
                    version.IsPrerelease,
                    version.IsListed,
                    version.CreatedAt)).ToList()))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<int, string>> GetVersionMetadataAsync(
        IReadOnlyCollection<int> versionIds, CancellationToken ct = default)
    {
        if (versionIds.Count == 0)
            return new Dictionary<int, string>();
        return await db.RegistryPackageVersions
            .AsNoTracking()
            .Where(version => versionIds.Contains(version.Id))
            .ToDictionaryAsync(version => version.Id, version => version.Metadata, ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<PackageRegistryPackageDto> Items, int Total)> GetPagedAsync(
        PackageRegistryKind? kind, string? search, int page, int pageSize, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.RegistryPackages.AsNoTracking().AsQueryable();
        if (kind.HasValue)
            query = query.Where(package => package.Kind == kind.Value);
        query = ApplySearch(query, search);
        query = Columns.ApplyFilters(query, columnFilters);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByProperty(sortBy, sortDescending,
                ordered => ordered.OrderBy(package => package.Kind).ThenBy(package => package.Name))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(package => new PackageRegistryPackageDto
            {
                Id = package.Id,
                Kind = package.Kind,
                Name = package.Name,
                Description = package.Description,
                LatestVersion = package.Versions
                    .Where(version => version.IsListed)
                    .OrderByDescending(version => version.CreatedAt)
                    .Select(version => version.Version)
                    .FirstOrDefault(),
                VersionCount = package.Versions.Count,
                TotalSizeBytes = package.Versions.Sum(version => version.SizeBytes),
                UpdatedAt = package.UpdatedAt
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, total);
    }

    public async Task AddPackageAsync(RegistryPackage package, CancellationToken ct = default)
        => await db.RegistryPackages.AddAsync(package, ct).ConfigureAwait(false);

    private static IQueryable<RegistryPackage> ApplySearch(
        IQueryable<RegistryPackage> query,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return query;

        var pattern = $"%{search.Trim()}%";
        return query.Where(package => EF.Functions.ILike(package.Name, pattern)
            || (package.Description != null && EF.Functions.ILike(package.Description, pattern)));
    }

    /// <summary>Every payload the database still references: the single archive of a version and,
    /// for multi-file formats, each of its files. The reconciler deletes what is not in this set.</summary>
    public async Task<HashSet<string>> GetStoredFilePathsAsync(CancellationToken ct = default)
    {
        var paths = (await db.RegistryPackageVersions.AsNoTracking()
                .Select(version => version.FilePath)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(await db.RegistryPackageFiles.AsNoTracking()
            .Select(file => file.FilePath)
            .ToListAsync(ct)
            .ConfigureAwait(false));
        return paths;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await db.SaveChangesAsync(ct).ConfigureAwait(false);
}
