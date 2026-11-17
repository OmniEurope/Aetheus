// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.VariableLibraries;

public interface IVariableLibraryRepository
{
    Task<(List<VariableLibrary> Items, int TotalCount)> GetLibrariesPagedAsync(
        string? search, int? projectId, int? environmentId, int? projectServerId, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false, IReadOnlyList<GridFilter>? columnFilters = null);

    Task<VariableLibraryFilterValuesDto> GetFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default);

    Task<VariableLibrary?> GetLibraryDetailAsync(int id, CancellationToken ct = default);

    Task<int> GetEntryCountAsync(int libraryId, CancellationToken ct = default);

    Task<(List<VariableLibraryEntry> Items, int TotalCount)> GetEntriesPagedAsync(
        int libraryId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null);

    Task<List<VariableLibraryEntry>> GetEntriesAsync(int libraryId, CancellationToken ct = default);

    Task<List<VariableLibrary>> FindByNamesAsync(List<string> names, int? projectId, CancellationToken ct = default);

    Task<List<VariableLibrary>> FindByNamesWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default);

    /// <summary>R-25: the libraries a run of the project can read that hold one of these keys, as (library id, library name,
    /// key). Key names only: no value is read. The project's own libraries come first.</summary>
    Task<List<(int Id, string Name, string Key)>> FindKeyHoldersWithCrossAccessAsync(
        IReadOnlyCollection<string> keys, int projectId, CancellationToken ct = default);

    Task<List<string>> GetLibraryNamesAsync(int? projectId, List<int>? accessibleIds = null, CancellationToken ct = default);

    Task<(List<string> Items, int TotalCount)> GetSuggestionKeysPagedAsync(
        int? projectId, string? search, int page, int pageSize, bool sortDescending,
        List<int>? accessibleIds = null, CancellationToken ct = default);

    Task<VariableLibrary?> FindLibraryAsync(int id, CancellationToken ct = default);

    Task AddLibraryAsync(VariableLibrary library, CancellationToken ct = default);

    Task RemoveLibraryAsync(VariableLibrary library, CancellationToken ct = default);

    Task<VariableLibraryEntry?> FindEntryAsync(int entryId, CancellationToken ct = default);

    /// <summary>Whether the library already holds <paramref name="key"/> on an entry other than <paramref name="exceptEntryId"/>.</summary>
    Task<bool> EntryKeyExistsAsync(int libraryId, string key, int? exceptEntryId, CancellationToken ct = default);

    Task AddEntryAsync(VariableLibraryEntry entry, CancellationToken ct = default);

    Task AddEntriesRangeAsync(List<VariableLibraryEntry> entries, CancellationToken ct = default);

    Task RemoveEntryAsync(VariableLibraryEntry entry, CancellationToken ct = default);

    Task AddEntryVersionAsync(VariableLibraryEntryVersion version, CancellationToken ct = default);

    Task AddEntryVersionsRangeAsync(List<VariableLibraryEntryVersion> versions, CancellationToken ct = default);

    Task<(List<VariableLibraryEntryVersion> Items, int TotalCount)> GetEntryVersionsPagedAsync(
        int entryId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null);

    Task<int> GetNextVersionAsync(int entryId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
