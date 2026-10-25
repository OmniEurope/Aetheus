// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>AI runners, AI tasks and their consumption accounting.</summary>
public sealed class AiApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<PaginatedResult<AiRunnerProfileDto>> GetAiRunnerProfilesAsync(
        int page = 1, int pageSize = 100, string? search = null, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null, string? sortBy = null, bool sortDescending = false)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy ?? "Name", sortDescending);
        // The list's column header filters.
        var url = GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/ai/profiles", query), filters);
        return await Http.GetFromJsonAsync<PaginatedResult<AiRunnerProfileDto>>(url, JsonOptions.Web, ct) ?? new();
    }


    public Task<AiRunnerProfileDto?> GetAiRunnerProfileAsync(int id, CancellationToken ct = default)
        => GetJsonAsync<AiRunnerProfileDto>($"api/ai/profiles/{id}", ct);


    public async Task<List<AiRunnerProfileDto>> GetAiRunnerProfileOptionsAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<AiRunnerProfileDto>>("api/ai/profile-options", ct).ConfigureAwait(false) ?? [];


    public Task<AiRunnerProfileDto?> CreateAiRunnerProfileAsync(
        CreateAiRunnerProfileRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateAiRunnerProfileRequest, AiRunnerProfileDto>("api/ai/profiles", request, ct);


    public Task<AiRunnerProfileDto?> UpdateAiRunnerProfileAsync(
        int id, UpdateAiRunnerProfileRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateAiRunnerProfileRequest, AiRunnerProfileDto>($"api/ai/profiles/{id}", request, ct);


    /// <summary>Runs one tiny completion from the backend with these values; the stored key is used
    /// when none is typed, and never comes back.</summary>
    public Task<ApiOutcome<AiConnectionTestDto, object>> TestAiRunnerProfileAsync(
        TestAiRunnerProfileRequest request, CancellationToken ct = default)
        => PostForOutcomeAsync<AiConnectionTestDto, object>("api/ai/profiles/test", request, ct);


    /// <summary>Recette R3-7: reads the saved profile's model list at its provider now and returns the
    /// profile as stored afterwards. 429 when it was read less than a minute ago.</summary>
    public Task<ApiOutcome<AiRunnerProfileDto, object>> RefreshAiRunnerProfileModelListAsync(
        int id, CancellationToken ct = default)
        => PostForOutcomeAsync<AiRunnerProfileDto, object>($"api/ai/profiles/{id}/model-list/refresh", null, ct);


    public Task<ApiStatus> DeleteAiRunnerProfileAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/ai/profiles/{id}", ct);


    public async Task<PaginatedResult<AiTaskDefinitionDto>> GetAiTasksAsync(
        int page = 1, int pageSize = 100, string? search = null,
        int? projectId = null, int? serverId = null, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null, string? sortBy = null, bool sortDescending = false)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy ?? "Name", sortDescending);
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        if (serverId.HasValue) query["serverId"] = serverId.Value.ToString();
        // The list's column header filters.
        var url = GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/ai/tasks", query), filters);
        return await Http.GetFromJsonAsync<PaginatedResult<AiTaskDefinitionDto>>(url, JsonOptions.Web, ct) ?? new();
    }


    public Task<AiTaskDefinitionDto?> GetAiTaskAsync(int id, CancellationToken ct = default)
        => GetJsonAsync<AiTaskDefinitionDto>($"api/ai/tasks/{id}", ct);


    public Task<AiTaskDefinitionDto?> CreateAiTaskAsync(
        CreateAiTaskDefinitionRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateAiTaskDefinitionRequest, AiTaskDefinitionDto>("api/ai/tasks", request, ct);


    public Task<AiTaskDefinitionDto?> UpdateAiTaskAsync(
        int id, UpdateAiTaskDefinitionRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateAiTaskDefinitionRequest, AiTaskDefinitionDto>($"api/ai/tasks/{id}", request, ct);


    public Task<ApiStatus> DeleteAiTaskAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/ai/tasks/{id}", ct);


    public Task<AiRunStartDto?> RunAiTaskAsync(int id, CancellationToken ct = default)
        => PostNoBodyAsync<AiRunStartDto>($"api/ai/tasks/{id}/run", ct);


    public async Task<PaginatedResult<AiRunResultDto>> GetAiResultsAsync(
        int page = 1, int pageSize = 25, int? definitionId = null,
        int? pipelineRunId = null, CancellationToken ct = default)
    {
        var query = BuildPaginationQuery(page, pageSize, null, "CreatedAt", true);
        if (definitionId.HasValue) query["definitionId"] = definitionId.Value.ToString();
        if (pipelineRunId.HasValue) query["pipelineRunId"] = pipelineRunId.Value.ToString();
        return await Http.GetFromJsonAsync<PaginatedResult<AiRunResultDto>>(
            QueryHelpers.AddQueryString("api/ai/results", query), JsonOptions.Web, ct) ?? new();
    }


    public Task<AiConsumptionDto?> GetAiConsumptionAsync(
        int? projectId = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>();
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        return GetJsonAsync<AiConsumptionDto>(
            QueryHelpers.AddQueryString("api/ai/consumption", query), ct);
    }
}
