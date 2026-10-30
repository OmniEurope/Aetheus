// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.VariableLibraries;

public class VariableLibraryRepository(AppDbContext db) : IVariableLibraryRepository
{
    /// <summary>The header filters of the variable libraries list.</summary>
    internal static readonly GridQueryMap<VariableLibrary> Columns = new GridQueryMap<VariableLibrary>()
        .Text("Name", vl => vl.Name)
        .Text("ProjectName", vl => vl.Project != null ? vl.Project.Name : null)
        .Text("Description", vl => vl.Description)
        .Number("EntryCount", vl => vl.Entries.Count)
        .Date("CreatedAt", vl => vl.CreatedAt)
        .Date("UpdatedAt", vl => vl.UpdatedAt);

    /// <summary>The header filters of a library's entries grid.</summary>
    internal static readonly GridQueryMap<VariableLibraryEntry> EntryColumns = new GridQueryMap<VariableLibraryEntry>()
        .Text("Key", entry => entry.Key)
        .Text("Value", entry => entry.Value);

    /// <summary>The header filters of an entry's version history grid.</summary>
    internal static readonly GridQueryMap<VariableLibraryEntryVersion> EntryVersionColumns = new GridQueryMap<VariableLibraryEntryVersion>()
        .Number("Version", version => version.Version)
        .Text("Key", version => version.Key)
        .Text("Value", version => version.Value)
        .Enum("ChangeType", version => version.ChangeType)
        .Date("ChangedAt", version => version.ChangedAt);

    /// <summary>The project names present across the libraries the caller can read.</summary>
    public async Task<VariableLibraryFilterValuesDto> GetFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default)
    {
        var query = db.VariableLibraries.AsNoTracking().Where(vl => vl.Project != null);
        if (accessibleIds is not null)
            query = query.Where(vl => accessibleIds.Contains(vl.Id));
        var names = await query.Select(vl => vl.Project!.Name).Distinct().ToListAsync(ct).ConfigureAwait(false);
        return new VariableLibraryFilterValuesDto { ProjectNames = [.. names.Order(StringComparer.OrdinalIgnoreCase)] };
    }

    public async Task<(List<VariableLibrary> Items, int TotalCount)> GetLibrariesPagedAsync(
        string? search, int? projectId, int? environmentId, int? projectServerId, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.VariableLibraries.AsNoTracking().AsQueryable();

        if (accessibleIds is not null)
            query = query.Where(vl => accessibleIds.Contains(vl.Id));

        if (environmentId.HasValue)
            query = query.Where(vl => vl.EnvironmentId == environmentId.Value);
        else if (projectServerId.HasValue)
            query = query.Where(vl => vl.ProjectServerId == projectServerId.Value);
        else if (projectId.HasValue)
            query = query.Where(vl => vl.ProjectId == projectId.Value || vl.ProjectId == null);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(vl => vl.Name.Contains(search) || vl.Description.Contains(search));

        query = Columns.ApplyFilters(query, columnFilters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        var rows = query
            .Include(vl => vl.Project)
            .Include(vl => vl.Environment)
            .Include(vl => vl.ProjectServer)
            .Include(vl => vl.Entries);

        // The list shows the owning project as its own column, but ProjectName is not a property of
        // the entity: resolved by name it would miss and fall back to Name, so the sort header would
        // move the arrow and leave the rows untouched. Mapped to the navigation, as BackupRepository
        // and GitLightRepository already do for the same column.
        IQueryable<VariableLibrary> ordered = string.Equals(sortBy, "ProjectName", StringComparison.OrdinalIgnoreCase)
            ? sortDescending
                ? rows.OrderByDescending(vl => vl.Project!.Name)
                : rows.OrderBy(vl => vl.Project!.Name)
            : rows.OrderByProperty(sortBy, sortDescending, vl => vl.Name, fallbackDescending: false);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, totalCount);
    }

    public async Task<VariableLibrary?> GetLibraryDetailAsync(int id, CancellationToken ct = default)
    {
        return await db.VariableLibraries
            .Where(vl => vl.Id == id)
            .Include(vl => vl.Project)
            .Include(vl => vl.Environment)
            .Include(vl => vl.ProjectServer)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> GetEntryCountAsync(int libraryId, CancellationToken ct = default)
        => await db.VariableLibraryEntries
            .AsNoTracking()
            .CountAsync(entry => entry.VariableLibraryId == libraryId, ct)
            .ConfigureAwait(false);

    public async Task<(List<VariableLibraryEntry> Items, int TotalCount)> GetEntriesPagedAsync(
        int libraryId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.VariableLibraryEntries
            .AsNoTracking()
            .Where(entry => entry.VariableLibraryId == libraryId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(entry =>
                EF.Functions.ILike(entry.Key, pattern) || EF.Functions.ILike(entry.Value, pattern));
        }

        query = EntryColumns.ApplyFilters(query, columnFilters);
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        query = query.Include(entry => entry.Versions);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("key", false) => query.OrderBy(entry => entry.Key).ThenBy(entry => entry.Id),
            ("key", true) => query.OrderByDescending(entry => entry.Key).ThenBy(entry => entry.Id),
            ("value", false) => query.OrderBy(entry => entry.Value).ThenBy(entry => entry.Id),
            ("value", true) => query.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Id),
            _ => query.OrderBy(entry => entry.Key).ThenBy(entry => entry.Id)
        };
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, totalCount);
    }

    public async Task<List<VariableLibraryEntry>> GetEntriesAsync(int libraryId, CancellationToken ct = default)
        => await db.VariableLibraryEntries
            .AsNoTracking()
            .Where(entry => entry.VariableLibraryId == libraryId)
            .Include(entry => entry.Versions)
            .OrderBy(entry => entry.Key)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<List<VariableLibrary>> FindByNamesAsync(List<string> names, int? projectId, CancellationToken ct = default)
    {
        return await db.VariableLibraries
            .AsNoTracking()
            // Same scope as the vaults: the project's own libraries, those of its environments and project
            // servers, and the global ones; never another project's environment (RBAC audit, finding 1).
            .Where(vl => names.Contains(vl.Name)
                && ((vl.ProjectId != null && vl.ProjectId == projectId)
                    || (vl.ProjectId == null && vl.EnvironmentId != null && vl.Environment!.ProjectId == projectId)
                    || (vl.ProjectId == null && vl.ProjectServerId != null && vl.ProjectServer!.ProjectId == projectId)
                    || (vl.ProjectId == null && vl.EnvironmentId == null && vl.ProjectServerId == null)))
            .Include(vl => vl.Entries)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<VariableLibrary>> FindByNamesWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default)
    {
        // Through the run's own project only: a row naming another project and this project's environment
        // (written before the owner check) never reaches the run (RBAC audit, finding 1).
        return await db.VariableLibraries
            .AsNoTracking()
            .Where(vl => names.Contains(vl.Name)
                && (vl.ProjectId == projectId
                    || (vl.ProjectId == null && vl.EnvironmentId != null && vl.Environment!.ProjectId == projectId)
                    || (vl.ProjectId == null && vl.ProjectServerId != null && vl.ProjectServer!.ProjectId == projectId)
                    || (vl.ProjectId == null && vl.EnvironmentId == null && vl.ProjectServerId == null)))
            .Include(vl => vl.Entries)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<string>> GetLibraryNamesAsync(int? projectId, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = db.VariableLibraries
            .AsNoTracking()
            .Where(vl => vl.ProjectId == projectId || vl.ProjectId == null);

        // F-11: when caller is not Admin (accessibleIds != null), restrict to libraries they can read.
        if (accessibleIds is not null)
            query = query.Where(vl => accessibleIds.Contains(vl.Id));

        return await query
            .Select(vl => vl.Name)
            .OrderBy(n => n)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<string> Items, int TotalCount)> GetSuggestionKeysPagedAsync(
        int? projectId, string? search, int page, int pageSize, bool sortDescending,
        List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = db.VariableLibraryEntries
            .AsNoTracking()
            .Where(entry => entry.VariableLibrary.ProjectId == projectId
                || entry.VariableLibrary.ProjectId == null);
        if (accessibleIds is not null)
            query = query.Where(entry => accessibleIds.Contains(entry.VariableLibraryId));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(entry => EF.Functions.ILike(entry.Key, pattern));
        }

        var keys = query.Select(entry => entry.Key).Distinct();
        var totalCount = await keys.CountAsync(ct).ConfigureAwait(false);
        keys = sortDescending ? keys.OrderByDescending(key => key) : keys.OrderBy(key => key);
        var items = await keys
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, totalCount);
    }

    public async Task<VariableLibrary?> FindLibraryAsync(int id, CancellationToken ct = default)
    {
        return await db.VariableLibraries.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task AddLibraryAsync(VariableLibrary library, CancellationToken ct = default)
    {
        db.VariableLibraries.Add(library);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveLibraryAsync(VariableLibrary library, CancellationToken ct = default)
    {
        db.VariableLibraries.Remove(library);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<VariableLibraryEntry?> FindEntryAsync(int entryId, CancellationToken ct = default)
    {
        return await db.VariableLibraryEntries.FindAsync([entryId], ct).ConfigureAwait(false);
    }

    public async Task<bool> EntryKeyExistsAsync(int libraryId, string key, int? exceptEntryId, CancellationToken ct = default)
    {
        return await db.VariableLibraryEntries.AsNoTracking()
            .AnyAsync(e => e.VariableLibraryId == libraryId && e.Key == key && e.Id != exceptEntryId, ct)
            .ConfigureAwait(false);
    }

    public async Task AddEntryAsync(VariableLibraryEntry entry, CancellationToken ct = default)
    {
        db.VariableLibraryEntries.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddEntriesRangeAsync(List<VariableLibraryEntry> entries, CancellationToken ct = default)
    {
        db.VariableLibraryEntries.AddRange(entries);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveEntryAsync(VariableLibraryEntry entry, CancellationToken ct = default)
    {
        db.VariableLibraryEntries.Remove(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddEntryVersionAsync(VariableLibraryEntryVersion version, CancellationToken ct = default)
    {
        db.VariableLibraryEntryVersions.Add(version);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddEntryVersionsRangeAsync(List<VariableLibraryEntryVersion> versions, CancellationToken ct = default)
    {
        db.VariableLibraryEntryVersions.AddRange(versions);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<VariableLibraryEntryVersion> Items, int TotalCount)> GetEntryVersionsPagedAsync(
        int entryId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.VariableLibraryEntryVersions
            .AsNoTracking()
            .Where(version => version.VariableLibraryEntryId == entryId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(version =>
                EF.Functions.ILike(version.Key, pattern) || EF.Functions.ILike(version.Value, pattern));
        }

        query = EntryVersionColumns.ApplyFilters(query, columnFilters);
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("version", false) => query.OrderBy(version => version.Version),
            ("key", false) => query.OrderBy(version => version.Key).ThenBy(version => version.Version),
            ("key", true) => query.OrderByDescending(version => version.Key).ThenByDescending(version => version.Version),
            ("value", false) => query.OrderBy(version => version.Value).ThenBy(version => version.Version),
            ("value", true) => query.OrderByDescending(version => version.Value).ThenByDescending(version => version.Version),
            ("changetype", false) => query.OrderBy(version => version.ChangeType).ThenBy(version => version.Version),
            ("changetype", true) => query.OrderByDescending(version => version.ChangeType).ThenByDescending(version => version.Version),
            ("changedat", false) => query.OrderBy(version => version.ChangedAt).ThenBy(version => version.Version),
            ("changedat", true) => query.OrderByDescending(version => version.ChangedAt).ThenByDescending(version => version.Version),
            _ => query.OrderByDescending(version => version.Version)
        };
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, totalCount);
    }

    public async Task<int> GetNextVersionAsync(int entryId, CancellationToken ct = default)
    {
        var maxVersion = await db.VariableLibraryEntryVersions
            .Where(v => v.VariableLibraryEntryId == entryId)
            .MaxAsync(v => (int?)v.Version, ct)
            .ConfigureAwait(false);

        return (maxVersion ?? 0) + 1;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
