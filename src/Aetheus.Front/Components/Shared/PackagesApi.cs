// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>Package feeds and the package registry.</summary>
public sealed class PackagesApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<PaginatedResult<PackageFeedDto>> GetPackageFeedsAsync(
        int page, int pageSize, string? search = null, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var qs = $"?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) qs += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(sortBy)) qs += $"&sortBy={Uri.EscapeDataString(sortBy)}&sortDescending={sortDescending.ToString().ToLowerInvariant()}";
        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<PackageFeedDto>>(
            GridColumnFilters.AddTo($"api/package-feeds{qs}", filters), JsonOptions.Web, ct) ?? new PaginatedResult<PackageFeedDto>();
    }



    public async Task<PackageFeedDetailDto?> GetPackageFeedAsync(int id)
    {
        var response = await Http.GetAsync($"api/package-feeds/{id}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageFeedDetailDto>(JsonOptions.Web);
    }


    public Task<PackageFeedDto?> CreatePackageFeedAsync(CreatePackageFeedRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreatePackageFeedRequest, PackageFeedDto>("api/package-feeds", request, ct);


    public Task<ApiStatus> DeletePackageFeedAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/package-feeds/{id}", ct);


    public Task<PackageEntryDto?> AddPackageToFeedAsync(int feedId, AddPackageRequest request, CancellationToken ct = default)
        => PostJsonAsync<AddPackageRequest, PackageEntryDto>($"api/package-feeds/{feedId}/packages", request, ct);


    public Task<ApiStatus> RemovePackageFromFeedAsync(int feedId, int packageId, CancellationToken ct = default)
        => DeleteAsync($"api/package-feeds/{feedId}/packages/{packageId}", ct);


    public async Task<PackageFeedSyncResultDto?> SyncPackageFeedAsync(int feedId)
    {
        var response = await Http.PostAsync($"api/package-feeds/{feedId}/sync", null);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<PackageFeedSyncResultDto>(JsonOptions.Web);
    }

    public async Task<PaginatedResult<PackageRegistryPackageDto>> GetRegistryPackagesAsync(
        int page, int pageSize, string? search = null, PackageRegistryKind? kind = null,
        CancellationToken ct = default, string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        if (kind.HasValue) query["kind"] = kind.Value.ToString();
        // The grid's column header filters.
        return await GetJsonAsync<PaginatedResult<PackageRegistryPackageDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/package-registry", query), filters), ct)
            .ConfigureAwait(false) ?? new();
    }


    public async Task<PackageRegistryPackageDetailDto?> GetRegistryPackageAsync(
        int id, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"api/package-registry/{id}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageRegistryPackageDetailDto>(
            JsonOptions.Web, ct).ConfigureAwait(false);
    }


    public Task<ApiStatus> UpdateRegistryVersionAsync(
        int packageId, int versionId, bool isListed, CancellationToken ct = default)
        => PutJsonNoBodyAsync(
            $"api/package-registry/{packageId}/versions/{versionId}",
            new UpdatePackageRegistryVersionRequest { IsListed = isListed },

            ct);


    public async Task<PipelineLintSummaryDto?> GetLintSummaryAsync(int runId, CancellationToken ct = default)
    {
        try { return await Http.GetFromJsonAsync<PipelineLintSummaryDto>($"api/pipelines/runs/{runId}/lint", JsonOptions.Web, ct); }
        catch (HttpRequestException) { return null; }
    }


    public Task<PipelineYamlDefinition?> ValidatePipelineYamlAsync(string yaml, CancellationToken ct = default)
        => PostJsonAsync<ValidateYamlRequest, PipelineYamlDefinition>("api/pipelines/validate", new ValidateYamlRequest { Yaml = yaml }, ct);
}
