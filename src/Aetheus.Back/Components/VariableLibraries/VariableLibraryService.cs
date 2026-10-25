// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.VariableLibraries;

public class VariableLibraryService(IVariableLibraryRepository repo, IDbTransactionScope transaction, IAuditService audit, IEntityChangeNotifier notifier, TimeProvider timeProvider, IMemoryCache cache) : IVariableLibraryService
{
    private const string LibNamesCachePrefix = "varlibs:names:";
    private static readonly TimeSpan CacheDuration = BackendRuntimeDefaults.ReferenceDataCacheDuration;

    /// <summary>Shared by every instance of this scoped service: a name list cached while serving one
    /// request must be invalidated by another request that creates, renames or deletes a library. Bumping
    /// it changes every cache key, so the stale lists are never read again and simply expire.</summary>
    private static long s_namesGeneration;

    /// <summary>The project names the variable libraries list's checkable filter offers.</summary>
    public Task<VariableLibraryFilterValuesDto> GetFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default) =>
        repo.GetFilterValuesAsync(accessibleIds, ct);

    public async Task<PaginatedResult<VariableLibraryDto>> GetLibrariesAsync(int? projectId, int? environmentId = null, int? projectServerId = null, PaginationRequest? request = null, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        request ??= new PaginationRequest();
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetLibrariesPagedAsync(
            request.Search, projectId, environmentId, projectServerId, page, pageSize, accessibleIds, ct,
            request.SortBy, request.SortDescending, request.Filters).ConfigureAwait(false);

        return new PaginatedResult<VariableLibraryDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<VariableLibraryDetailDto?> GetLibraryDetailAsync(int id, CancellationToken ct = default)
    {
        var library = await repo.GetLibraryDetailAsync(id, ct).ConfigureAwait(false);
        if (library is null) return null;

        var entryCount = await repo.GetEntryCountAsync(id, ct).ConfigureAwait(false);

        return new VariableLibraryDetailDto
        {
            Id = library.Id,
            Name = library.Name,
            Description = library.Description,
            ProjectId = library.ProjectId,
            ProjectName = library.Project?.Name,
            EnvironmentId = library.EnvironmentId,
            EnvironmentName = library.Environment?.Name,
            ProjectServerId = library.ProjectServerId,
            ProjectServerName = library.ProjectServer?.DisplayName,
            EntryCount = entryCount,
            CreatedAt = library.CreatedAt,
            UpdatedAt = library.UpdatedAt,
            RowVersion = library.RowVersion
        };
    }

    public async Task<PaginatedResult<VariableEntryDto>> GetEntriesAsync(
        int libraryId, PaginationRequest request, CancellationToken ct = default)
    {
        if (await repo.FindLibraryAsync(libraryId, ct).ConfigureAwait(false) is null)
            throw new NotFoundException("Variable library not found");

        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetEntriesPagedAsync(
            libraryId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters)
            .ConfigureAwait(false);
        return new PaginatedResult<VariableEntryDto>
        {
            Items = items.Select(MapEntryToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<List<string>> GetLibraryNamesAsync(int? projectId, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var key = BuildNamesCacheKey(projectId, accessibleIds);
        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await repo.GetLibraryNamesAsync(projectId, accessibleIds, ct).ConfigureAwait(false);
        }).ConfigureAwait(false) ?? [];
    }

    public async Task<PaginatedResult<string>> GetSuggestionKeysAsync(
        int? projectId, PaginationRequest request, List<int>? accessibleIds = null,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetSuggestionKeysPagedAsync(
            projectId, request.Search, page, pageSize, request.SortDescending, accessibleIds, ct)
            .ConfigureAwait(false);
        return new PaginatedResult<string>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<VariableLibraryDto> CreateLibraryAsync(CreateVariableLibraryRequest request, CancellationToken ct = default)
    {
        var library = new VariableLibrary
        {
            Name = request.Name,
            Description = request.Description,
            ProjectId = request.ProjectId,
            EnvironmentId = request.EnvironmentId,
            ProjectServerId = request.ProjectServerId
        };

        await repo.AddLibraryAsync(library, ct).ConfigureAwait(false);
        InvalidateNamesCache();
        await audit.LogAsync("Created", "VariableLibrary", library.Id, library.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.VariableLibrary, library.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);
        return MapToDto(library);
    }

    public async Task<VariableLibraryDto?> UpdateLibraryAsync(int id, UpdateVariableLibraryRequest request, CancellationToken ct = default)
    {
        var library = await repo.GetLibraryDetailAsync(id, ct).ConfigureAwait(false);
        if (library is null) return null;

        if (request.RowVersion != library.RowVersion)
            throw new ConflictException("The variable library was modified by another user. Please reload and try again.");

        library.Name = request.Name;
        library.Description = request.Description;
        library.ProjectId = request.ProjectId;
        library.EnvironmentId = request.EnvironmentId;
        library.ProjectServerId = request.ProjectServerId;
        library.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        library.RowVersion = Guid.NewGuid();

        try
        {
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The variable library was modified by another user. Please reload and try again.");
        }

        InvalidateNamesCache();
        await audit.LogAsync("Updated", "VariableLibrary", library.Id, library.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.VariableLibrary, library.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return MapToDto(library);
    }

    public async Task<bool> DeleteLibraryAsync(int id, CancellationToken ct = default)
    {
        var library = await repo.FindLibraryAsync(id, ct).ConfigureAwait(false);
        if (library is null) return false;

        await repo.RemoveLibraryAsync(library, ct).ConfigureAwait(false);
        InvalidateNamesCache();
        await audit.LogAsync("Deleted", "VariableLibrary", id, library.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.VariableLibrary, id, EntityChangeOps.Deleted, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Reject duplicate library keys with an explicit reason before relying on the unique
    /// index.</summary>
    private async Task EnsureKeyIsFreeAsync(int libraryId, string key, int? exceptEntryId, CancellationToken ct)
    {
        if (await repo.EntryKeyExistsAsync(libraryId, key, exceptEntryId, ct).ConfigureAwait(false))
            throw new BadRequestException($"The key '{key}' already exists in this library.");
    }

    public async Task<VariableEntryDto> CreateEntryAsync(int libraryId, CreateVariableEntryRequest request, CancellationToken ct = default)
    {
        var library = await repo.FindLibraryAsync(libraryId, ct).ConfigureAwait(false);
        if (library is null) throw new NotFoundException("Variable library not found");
        await EnsureKeyIsFreeAsync(libraryId, request.Key, null, ct).ConfigureAwait(false);

        var entry = new VariableLibraryEntry
        {
            VariableLibraryId = libraryId,
            Key = request.Key,
            Value = request.Value
        };

        await transaction.ExecuteInTransactionAsync(async () =>
        {
            await repo.AddEntryAsync(entry, ct).ConfigureAwait(false);

            await AddEntryVersionAsync(entry, ChangeType.Created, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return new VariableEntryDto { Id = entry.Id, Key = entry.Key, Value = entry.Value };
    }

    public async Task<VariableEntryDto?> UpdateEntryAsync(int libraryId, int entryId, UpdateVariableEntryRequest request, CancellationToken ct = default)
    {
        var entry = await repo.FindEntryAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null || entry.VariableLibraryId != libraryId) return null;
        await EnsureKeyIsFreeAsync(libraryId, request.Key, entryId, ct).ConfigureAwait(false);

        entry.Key = request.Key;
        entry.Value = request.Value;

        await transaction.ExecuteInTransactionAsync(async () =>
        {
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);

            await AddEntryVersionAsync(entry, ChangeType.Updated, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return new VariableEntryDto { Id = entry.Id, Key = entry.Key, Value = entry.Value };
    }

    public async Task<bool> DeleteEntryAsync(int libraryId, int entryId, CancellationToken ct = default)
    {
        var entry = await repo.FindEntryAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null || entry.VariableLibraryId != libraryId) return false;

        await transaction.ExecuteInTransactionAsync(async () =>
        {
            await AddEntryVersionAsync(entry, ChangeType.Deleted, ct).ConfigureAwait(false);

            await repo.RemoveEntryAsync(entry, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return true;
    }

    public async Task<PaginatedResult<VariableEntryVersionDto>> GetEntryVersionsAsync(
        int libraryId, int entryId, PaginationRequest request, CancellationToken ct = default)
    {
        var entry = await repo.FindEntryAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null || entry.VariableLibraryId != libraryId)
            throw new NotFoundException("Entry not found");

        var (page, pageSize) = request.Normalize();
        var (versions, totalCount) = await repo.GetEntryVersionsPagedAsync(
            entryId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters)
            .ConfigureAwait(false);
        return new PaginatedResult<VariableEntryVersionDto>
        {
            Items = versions.Select(v => new VariableEntryVersionDto
            {
                Version = v.Version,
                Key = v.Key,
                Value = v.Value,
                ChangedAt = v.ChangedAt,
                ChangeType = v.ChangeType
            }).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<Dictionary<string, string>> ResolveLibrariesAsync(List<string> names, int? projectId, CancellationToken ct = default)
    {
        var libraries = await repo.FindByNamesAsync(names, projectId, ct).ConfigureAwait(false);

        return MergeByPrecedence(libraries, library => library.ProjectId is null, library => library.ProjectId is not null);
    }

    public async Task<Dictionary<string, string>> ResolveLibrariesWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default)
    {
        var libraries = await repo.FindByNamesWithCrossAccessAsync(names, projectId, ct).ConfigureAwait(false);
        return MergeWithPrecedence(libraries);
    }

    private static readonly Func<Data.Entities.VariableLibrary, bool>[] CrossAccessScopes =
    [
        library => library.ProjectId is null && library.EnvironmentId is null && library.ProjectServerId is null,
        library => library.ProjectId is not null,
        library => library.EnvironmentId is not null,
        library => library.ProjectServerId is not null
    ];

    private static Dictionary<string, string> MergeWithPrecedence(List<Data.Entities.VariableLibrary> libraries) =>
        MergeByPrecedence(libraries, CrossAccessScopes);

    public async Task<Dictionary<string, int>> ResolveEntryOwnersWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default)
    {
        var libraries = await repo.FindByNamesWithCrossAccessAsync(names, projectId, ct).ConfigureAwait(false);
        var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Same walk as the merge, so the last library to set a key (the one whose value wins) owns it.
        foreach (var scope in CrossAccessScopes)
            foreach (var library in libraries.Where(scope))
                foreach (var entry in library.Entries)
                    owners[entry.Key] = library.Id;
        return owners;
    }

    public async Task<(Dictionary<string, string> Vars, HashSet<string> FoundNames)> ResolveLibrariesWithNamesAsync(List<string> names, int? projectId, CancellationToken ct = default)
    {
        var libraries = await repo.FindByNamesAsync(names, projectId, ct).ConfigureAwait(false);
        var foundNames = new HashSet<string>(libraries.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);

        var variables = MergeByPrecedence(
            libraries,
            library => library.ProjectId is null,
            library => library.ProjectId is not null);
        return (variables, foundNames);
    }

    public async Task<(Dictionary<string, string> Vars, HashSet<string> FoundNames)> ResolveLibrariesWithCrossAccessAndNamesAsync(List<string> names, int projectId, CancellationToken ct = default)
    {
        var libraries = await repo.FindByNamesWithCrossAccessAsync(names, projectId, ct).ConfigureAwait(false);
        var foundNames = new HashSet<string>(libraries.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
        return (MergeWithPrecedence(libraries), foundNames);
    }

    private async Task AddEntryVersionAsync(
        VariableLibraryEntry entry,
        ChangeType changeType,
        CancellationToken ct)
    {
        var version = await repo.GetNextVersionAsync(entry.Id, ct).ConfigureAwait(false);
        await repo.AddEntryVersionAsync(new VariableLibraryEntryVersion
        {
            VariableLibraryEntryId = entry.Id,
            Key = entry.Key,
            Value = entry.Value,
            ChangedAt = timeProvider.GetUtcNow().UtcDateTime,
            Version = version,
            ChangeType = changeType
        }, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> MergeByPrecedence(
        IReadOnlyCollection<Data.Entities.VariableLibrary> libraries,
        params Func<Data.Entities.VariableLibrary, bool>[] scopes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in scopes)
        {
            foreach (var library in libraries.Where(scope))
                foreach (var entry in library.Entries)
                    result[entry.Key] = entry.Value;
        }

        return result;
    }

    public async Task<List<VariableEntryDto>> ExportEntriesAsync(int libraryId, CancellationToken ct = default)
    {
        if (await repo.FindLibraryAsync(libraryId, ct).ConfigureAwait(false) is null)
            throw new NotFoundException("Variable library not found");

        var entries = await repo.GetEntriesAsync(libraryId, ct).ConfigureAwait(false);
        return entries.Select(MapEntryToDto).ToList();
    }

    public async Task<int> ImportEntriesAsync(int libraryId, List<CreateVariableEntryRequest> entries, CancellationToken ct = default)
    {
        var library = await repo.FindLibraryAsync(libraryId, ct).ConfigureAwait(false);
        if (library is null) throw new NotFoundException("Variable library not found");

        await transaction.ExecuteInTransactionAsync(async () =>
        {
            var createdEntries = entries.Select(req => new VariableLibraryEntry
            {
                VariableLibraryId = libraryId,
                Key = req.Key,
                Value = req.Value
            }).ToList();

            await repo.AddEntriesRangeAsync(createdEntries, ct).ConfigureAwait(false);

            var versions = createdEntries.Select(entry => new VariableLibraryEntryVersion
            {
                VariableLibraryEntryId = entry.Id,
                Key = entry.Key,
                Value = entry.Value,
                ChangedAt = timeProvider.GetUtcNow().UtcDateTime,
                Version = 1,
                ChangeType = ChangeType.Created
            }).ToList();

            await repo.AddEntryVersionsRangeAsync(versions, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return entries.Count;
    }

    private static string BuildNamesCacheKey(int? projectId, List<int>? accessibleIds)
    {
        var pid = projectId?.ToString() ?? "all";
        var generation = Volatile.Read(ref s_namesGeneration);
        if (accessibleIds is null or [])
            return $"{LibNamesCachePrefix}{generation}:{pid}";
        return $"{LibNamesCachePrefix}{generation}:{pid}:{string.Join(',', accessibleIds.Order())}";
    }

    private static void InvalidateNamesCache() => Interlocked.Increment(ref s_namesGeneration);

    private static VariableLibraryDto MapToDto(VariableLibrary vl) => new()
    {
        Id = vl.Id,
        Name = vl.Name,
        Description = vl.Description,
        ProjectId = vl.ProjectId,
        ProjectName = vl.Project?.Name,
        EnvironmentId = vl.EnvironmentId,
        EnvironmentName = vl.Environment?.Name,
        ProjectServerId = vl.ProjectServerId,
        ProjectServerName = vl.ProjectServer?.DisplayName,
        EntryCount = vl.Entries.Count,
        CreatedAt = vl.CreatedAt,
        UpdatedAt = vl.UpdatedAt,
        RowVersion = vl.RowVersion
    };

    private static VariableEntryDto MapEntryToDto(VariableLibraryEntry entry) => new()
    {
        Id = entry.Id,
        Key = entry.Key,
        Value = entry.Value,
        VersionCount = entry.Versions.Count
    };
}
