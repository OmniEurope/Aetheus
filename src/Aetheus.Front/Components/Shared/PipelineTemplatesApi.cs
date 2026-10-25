// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Pipeline templates and their versions. The only sub-client holding state: a template list cache,
/// invalidated by every writer of this client (create, update, delete, import, extract, promote) and,
/// FRONT-012, bounded to <see cref="CacheLifetime"/> so a change made in another session (seen by no
/// writer here) reaches the pages that never listen to the hub.
/// </summary>
public sealed class PipelineTemplatesApi(HttpClient http, TimeProvider? time = null) : ApiClientBase(http)
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private List<PipelineTemplateSummaryDto>? _templateCache;
    private DateTimeOffset _templateCachedAt;


    public async Task<List<PipelineTemplateSummaryDto>> GetPipelineTemplatesAsync()
    {
        if (_templateCache is not null && _time.GetUtcNow() - _templateCachedAt < CacheLifetime) return _templateCache;
        var templates = await Http.GetFromJsonAsync<List<PipelineTemplateSummaryDto>>("api/pipelines/templates", JsonOptions.Web) ?? [];
        _templateCache = templates;
        _templateCachedAt = _time.GetUtcNow();
        return templates;
    }


    public async Task<PipelineTemplateDto?> ExtractPipelineTemplateAsync(
        int pipelineId, ExtractPipelineTemplateRequest request, CancellationToken ct = default)
    {
        var dto = await PostJsonAsync<ExtractPipelineTemplateRequest, PipelineTemplateDto>(
            $"api/pipelines/{pipelineId}/extract-template", request, ct).ConfigureAwait(false);
        if (dto != null) InvalidateTemplateCache();
        return dto;
    }


    public async Task<PipelineTemplateDto?> PromotePipelineTemplateAsync(
        int pipelineId, PromotePipelineTemplateRequest request, CancellationToken ct = default)
    {
        var dto = await PostJsonAsync<PromotePipelineTemplateRequest, PipelineTemplateDto>(
            $"api/pipelines/{pipelineId}/promote-template", request, ct).ConfigureAwait(false);
        if (dto != null) InvalidateTemplateCache();
        return dto;
    }


    public void InvalidateTemplateCache() => _templateCache = null;


    public async Task<PipelineTemplateDto?> GetPipelineTemplateAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<PipelineTemplateDto>($"api/pipelines/templates/{id}", JsonOptions.Web, ct);
    }


    public async Task<PaginatedResult<PipelineTemplateVersionSummaryDto>> GetPipelineTemplateVersionsAsync(
        int id, int page, int pageSize, string? sortBy = null, bool sortDescending = true,
        CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, null, sortBy, sortDescending);
        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<PipelineTemplateVersionSummaryDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/pipelines/templates/{id}/versions", query), filters),
            JsonOptions.Web, ct) ?? new();
    }


    public async Task<PipelineTemplateVersionDto?> GetPipelineTemplateVersionAsync(
        int id, int version, CancellationToken ct = default)
        => await Http.GetFromJsonAsync<PipelineTemplateVersionDto>(
            $"api/pipelines/templates/{id}/versions/{version}", JsonOptions.Web, ct);


    public async Task<string?> ResolvePipelineTemplateAsync(
        int id, int version, Dictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        using var response = await Http.PostAsJsonAsync(
            $"api/pipelines/templates/{id}/resolve?version={version}", parameters, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
            : null;
    }


    public async Task<PipelineTemplateDto?> CreatePipelineTemplateAsync(CreatePipelineTemplateRequest request, CancellationToken ct = default)
    {
        var dto = await PostJsonAsync<CreatePipelineTemplateRequest, PipelineTemplateDto>("api/pipelines/templates", request, ct).ConfigureAwait(false);
        if (dto != null) InvalidateTemplateCache();
        return dto;
    }


    public async Task<PipelineTemplateDto?> UpdatePipelineTemplateAsync(int id, UpdatePipelineTemplateRequest request, CancellationToken ct = default)
    {
        var dto = await PutJsonAsync<UpdatePipelineTemplateRequest, PipelineTemplateDto>($"api/pipelines/templates/{id}", request, ct).ConfigureAwait(false);
        if (dto != null) InvalidateTemplateCache();
        return dto;
    }


    public async Task<ApiStatus> DeletePipelineTemplateAsync(int id, CancellationToken ct = default)
    {
        var ok = await DeleteAsync($"api/pipelines/templates/{id}", ct).ConfigureAwait(false);
        if (ok) InvalidateTemplateCache();
        return ok;
    }


    public async Task<byte[]?> ExportPipelineTemplateAsync(int id, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/pipelines/templates/{id}/export", ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync();
    }


    public async Task<PipelineTemplateDto?> ImportPipelineTemplateAsync(byte[] fileContent, string fileName, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(fileContent), "file", fileName);
        var response = await Http.PostAsync("api/pipelines/templates/import", content, ct);
        if (!response.IsSuccessStatusCode) return null;
        InvalidateTemplateCache();
        return await response.Content.ReadFromJsonAsync<PipelineTemplateDto>(JsonOptions.Web);
    }
}
