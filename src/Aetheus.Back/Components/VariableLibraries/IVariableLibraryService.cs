// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.VariableLibraries;

public interface IVariableLibraryService
{
    Task<VariableLibraryFilterValuesDto> GetFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default);
    Task<PaginatedResult<VariableLibraryDto>> GetLibrariesAsync(int? projectId, int? environmentId = null, int? projectServerId = null, PaginationRequest? request = null, List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<VariableLibraryDetailDto?> GetLibraryDetailAsync(int id, CancellationToken ct = default);
    Task<PaginatedResult<VariableEntryDto>> GetEntriesAsync(int libraryId, PaginationRequest request, CancellationToken ct = default);
    Task<List<string>> GetLibraryNamesAsync(int? projectId, List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<PaginatedResult<string>> GetSuggestionKeysAsync(
        int? projectId, PaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<VariableLibraryDto> CreateLibraryAsync(CreateVariableLibraryRequest request, CancellationToken ct = default);
    Task<VariableLibraryDto?> UpdateLibraryAsync(int id, UpdateVariableLibraryRequest request, CancellationToken ct = default);
    Task<bool> DeleteLibraryAsync(int id, CancellationToken ct = default);
    Task<VariableEntryDto> CreateEntryAsync(int libraryId, CreateVariableEntryRequest request, CancellationToken ct = default);
    Task<VariableEntryDto?> UpdateEntryAsync(int libraryId, int entryId, UpdateVariableEntryRequest request, CancellationToken ct = default);
    Task<bool> DeleteEntryAsync(int libraryId, int entryId, CancellationToken ct = default);
    Task<PaginatedResult<VariableEntryVersionDto>> GetEntryVersionsAsync(
        int libraryId, int entryId, PaginationRequest request, CancellationToken ct = default);
    Task<Dictionary<string, string>> ResolveLibrariesAsync(List<string> names, int? projectId, CancellationToken ct = default);
    Task<Dictionary<string, string>> ResolveLibrariesWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default);
    Task<(Dictionary<string, string> Vars, HashSet<string> FoundNames)> ResolveLibrariesWithNamesAsync(List<string> names, int? projectId, CancellationToken ct = default);
    Task<(Dictionary<string, string> Vars, HashSet<string> FoundNames)> ResolveLibrariesWithCrossAccessAndNamesAsync(List<string> names, int projectId, CancellationToken ct = default);
    /// <summary>For each entry key of the named libraries, the id of the library whose value a run of the project
    /// would read (same precedence and cross-project access as <see cref="ResolveLibrariesWithCrossAccessAndNamesAsync"/>).</summary>
    Task<Dictionary<string, int>> ResolveEntryOwnersWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default);
    Task<List<VariableEntryDto>> ExportEntriesAsync(int libraryId, CancellationToken ct = default);
    Task<int> ImportEntriesAsync(int libraryId, List<CreateVariableEntryRequest> entries, CancellationToken ct = default);
}
