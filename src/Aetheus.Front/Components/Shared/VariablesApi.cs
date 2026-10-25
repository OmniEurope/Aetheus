// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>Variable libraries and vaults.</summary>
public sealed class VariablesApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<PaginatedResult<VariableLibraryDto>> GetVariableLibrariesAsync(
        int page = 1, int pageSize = 25, string? search = null, int? projectId = null, int? environmentId = null, int? projectServerId = null,
        string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = ScopedPageQuery(page, pageSize, search, sortBy, sortDescending, projectId, environmentId, projectServerId);
        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<VariableLibraryDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/variable-libraries", query), filters), JsonOptions.Web) ?? new();
    }

    /// <summary>The project names the variable libraries list's checkable filter offers.</summary>
    public async Task<VariableLibraryFilterValuesDto> GetVariableLibraryFilterValuesAsync(CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<VariableLibraryFilterValuesDto>("api/variable-libraries/filter-values", JsonOptions.Web, ct) ?? new();


    public async Task<VariableLibraryDetailDto?> GetVariableLibraryDetailAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<VariableLibraryDetailDto>($"api/variable-libraries/{id}", JsonOptions.Web, ct);
    }


    public async Task<List<VariableLibraryDto>> GetAllVariableLibrariesAsync(int? projectId = null)
    {
        const int pageSize = 100;
        var items = new List<VariableLibraryDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetVariableLibrariesAsync(page, pageSize, projectId: projectId);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public async Task<List<string>> GetVariableLibraryNamesAsync(int? projectId = null)
    {
        var query = new Dictionary<string, string?>();
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        return await Http.GetFromJsonAsync<List<string>>(
            QueryHelpers.AddQueryString("api/variable-libraries/names", query), JsonOptions.Web) ?? [];
    }


    public async Task<PaginatedResult<string>> GetVariableSuggestionKeysAsync(
        int page, int pageSize, int? projectId = null, string? search = null,
        CancellationToken ct = default)
    {
        var query = BuildPaginationQuery(page, pageSize, search, "Key", false);
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        return await Http.GetFromJsonAsync<PaginatedResult<string>>(
            QueryHelpers.AddQueryString("api/variable-libraries/suggestion-keys", query),
            JsonOptions.Web, ct) ?? new();
    }


    public Task<VariableLibraryDto?> CreateVariableLibraryAsync(CreateVariableLibraryRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateVariableLibraryRequest, VariableLibraryDto>("api/variable-libraries", request, ct);


    public Task<VariableLibraryDto?> UpdateVariableLibraryAsync(int id, UpdateVariableLibraryRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateVariableLibraryRequest, VariableLibraryDto>($"api/variable-libraries/{id}", request, ct);


    public Task<ApiStatus> DeleteVariableLibraryAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/variable-libraries/{id}", ct);


    /// <summary>Return duplicate-key refusal as HTTP 400 so the caller can explain why the entry
    /// was rejected.</summary>
    public Task<ApiOutcome<VariableEntryDto, ApiError>> CreateVariableEntryAsync(int libraryId, CreateVariableEntryRequest request, CancellationToken ct = default)
        => PostForOutcomeAsync<VariableEntryDto, ApiError>($"api/variable-libraries/{libraryId}/entries", request, ct);


    /// <inheritdoc cref="CreateVariableEntryAsync"/>
    public Task<ApiOutcome<VariableEntryDto, ApiError>> UpdateVariableEntryAsync(int libraryId, int entryId, UpdateVariableEntryRequest request, CancellationToken ct = default)
        => PutForOutcomeAsync<VariableEntryDto, ApiError>($"api/variable-libraries/{libraryId}/entries/{entryId}", request, ct);


    public Task<ApiStatus> DeleteVariableEntryAsync(int libraryId, int entryId, CancellationToken ct = default)
        => DeleteAsync($"api/variable-libraries/{libraryId}/entries/{entryId}", ct);


    public async Task<PaginatedResult<VariableEntryDto>> GetVariableEntriesPageAsync(
        int libraryId, int page, int pageSize, string? search = null, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<VariableEntryDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/variable-libraries/{libraryId}/entries", query), filters),
            JsonOptions.Web, ct) ?? new();
    }


    public async Task<PaginatedResult<VariableEntryVersionDto>> GetVariableEntryVersionsPageAsync(
        int libraryId, int entryId, int page, int pageSize, string? search = null,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<VariableEntryVersionDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString(
                $"api/variable-libraries/{libraryId}/entries/{entryId}/versions", query), filters), JsonOptions.Web, ct) ?? new();
    }


    public async Task<List<VariableEntryDto>> ExportVariableEntriesAsync(int libraryId)
    {
        return await Http.GetFromJsonAsync<List<VariableEntryDto>>($"api/variable-libraries/{libraryId}/export", JsonOptions.Web) ?? [];
    }


    public Task<ImportResultDto?> ImportVariableEntriesAsync(int libraryId, List<CreateVariableEntryRequest> entries, CancellationToken ct = default)
        => PostJsonAsync<List<CreateVariableEntryRequest>, ImportResultDto>($"api/variable-libraries/{libraryId}/import", entries, ct);

    public async Task<PaginatedResult<VaultDto>> GetVaultsAsync(
        int page = 1, int pageSize = 25, string? search = null, int? projectId = null, int? environmentId = null, int? projectServerId = null,
        string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = ScopedPageQuery(page, pageSize, search, sortBy, sortDescending, projectId, environmentId, projectServerId);
        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<VaultDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/vaults", query), filters), JsonOptions.Web) ?? new();
    }

    /// <summary>Who reads each library and vault the caller can read: pipelines and monitored applications (R-65, R-66).</summary>
    public async Task<ResourceUsageDto> GetResourceUsageAsync(CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<ResourceUsageDto>("api/pipelines/resource-usage", JsonOptions.Web, ct) ?? new();

    /// <summary>The project names the vaults list's checkable filter offers.</summary>
    public async Task<VaultFilterValuesDto> GetVaultFilterValuesAsync(CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<VaultFilterValuesDto>("api/vaults/filter-values", JsonOptions.Web, ct) ?? new();


    public async Task<VaultDetailDto?> GetVaultDetailAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<VaultDetailDto>($"api/vaults/{id}", JsonOptions.Web, ct);
    }


    public async Task<List<string>> GetVaultNamesAsync(int? projectId = null)
    {
        var query = new Dictionary<string, string?>();
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        return await Http.GetFromJsonAsync<List<string>>(
            QueryHelpers.AddQueryString("api/vaults/names", query), JsonOptions.Web) ?? [];
    }


    public Task<VaultDto?> CreateVaultAsync(CreateVaultRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateVaultRequest, VaultDto>("api/vaults", request, ct);


    public Task<VaultDto?> UpdateVaultAsync(int id, UpdateVaultRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateVaultRequest, VaultDto>($"api/vaults/{id}", request, ct);


    public Task<ApiStatus> DeleteVaultAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/vaults/{id}", ct);


    public Task<VaultSecretDto?> CreateVaultSecretAsync(int vaultId, CreateVaultSecretRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateVaultSecretRequest, VaultSecretDto>($"api/vaults/{vaultId}/secrets", request, ct);


    public Task<VaultSecretDto?> UpdateVaultSecretAsync(int vaultId, int secretId, UpdateVaultSecretRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateVaultSecretRequest, VaultSecretDto>($"api/vaults/{vaultId}/secrets/{secretId}", request, ct);


    public Task<VaultSecretDto?> RotateVaultSecretAsync(int vaultId, int secretId, RotateVaultSecretRequest request, CancellationToken ct = default)
        => PostJsonAsync<RotateVaultSecretRequest, VaultSecretDto>($"api/vaults/{vaultId}/secrets/{secretId}/rotate", request, ct);


    /// <summary>One secret's clear value, for the copy button (POST, audited, no-store).</summary>
    public Task<RevealedSecretValueDto?> RevealVaultSecretValueAsync(int vaultId, int secretId, CancellationToken ct = default)
        => PostJsonAsync<object, RevealedSecretValueDto>($"api/vaults/{vaultId}/secrets/{secretId}/reveal", new { }, ct);

    public Task<ApiStatus> DeleteVaultSecretAsync(int vaultId, int secretId, CancellationToken ct = default)
        => DeleteAsync($"api/vaults/{vaultId}/secrets/{secretId}", ct);


    public async Task<List<VaultSecretVersionDto>> GetVaultSecretVersionsAsync(int vaultId, int secretId)
    {
        return await Http.GetFromJsonAsync<List<VaultSecretVersionDto>>($"api/vaults/{vaultId}/secrets/{secretId}/versions", JsonOptions.Web) ?? [];
    }


    public async Task<List<string>> ExportVaultSecretKeysAsync(int vaultId)
    {
        return await Http.GetFromJsonAsync<List<string>>($"api/vaults/{vaultId}/export-keys", JsonOptions.Web) ?? [];
    }


    public Task<ImportResultDto?> ImportVaultSecretsAsync(int vaultId, List<CreateVaultSecretRequest> secrets, CancellationToken ct = default)
        => PostJsonAsync<List<CreateVaultSecretRequest>, ImportResultDto>($"api/vaults/{vaultId}/import", secrets, ct);


    /// <summary>Where this library may allocate, and why it may not when it cannot.</summary>
    public Task<PortAllocationTargetsDto?> GetPortAllocationTargetsAsync(
        int libraryId, CancellationToken ct = default)
        => GetJsonAsync<PortAllocationTargetsDto>($"api/variable-libraries/{libraryId}/ports/servers", ct);


    /// <summary>
    /// Allocates one free port per key and writes the entries. The outcome carries the API error
    /// because every refusal here is actionable: no project on the library, a key without the
    /// <c>PORT_</c> prefix, or a window with too few free ports.
    /// </summary>
    public Task<ApiOutcome<List<VariableEntryDto>, ApiError>> AllocateLibraryPortsAsync(
        int libraryId, AllocateLibraryPortsRequest request, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<AllocateLibraryPortsRequest, List<VariableEntryDto>>(
            $"api/variable-libraries/{libraryId}/ports/allocate", request, ct);


    /// <summary>Releases a port this library's project holds, after its PORT_* entry was deleted.</summary>
    public async Task<int> ReleaseLibraryPortAsync(int libraryId, int port, CancellationToken ct = default)
    {
        var response = await Http.DeleteAsync($"api/variable-libraries/{libraryId}/ports/{port}", ct);
        if (!response.IsSuccessStatusCode) return 0;
        return await response.Content.ReadFromJsonAsync<int>(JsonOptions.Web, ct).ConfigureAwait(false);
    }


    /// <summary>Checks this library's existing <c>PORT_*</c> entries against one server.</summary>
    public Task<ApiOutcome<PortCheckResultDto, ApiError>> CheckLibraryPortsAsync(
        int libraryId, AllocateLibraryPortsRequest request, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<AllocateLibraryPortsRequest, PortCheckResultDto>(
            $"api/variable-libraries/{libraryId}/ports/check", request, ct);

    /// <summary>The page query of the libraries and vaults lists, narrowed to the scope they are shown in.</summary>
    private static Dictionary<string, string?> ScopedPageQuery(int page, int pageSize, string? search, string? sortBy,
        bool sortDescending, int? projectId, int? environmentId, int? projectServerId)
    {
        var query = PageQuery(page, pageSize, search, sortBy, sortDescending);
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        if (environmentId.HasValue) query["environmentId"] = environmentId.Value.ToString();
        if (projectServerId.HasValue) query["projectServerId"] = projectServerId.Value.ToString();
        return query;
    }
}
