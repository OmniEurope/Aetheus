// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Projects;

/// <summary>Projects and their releases.</summary>
public sealed class ProjectsApi(HttpClient http) : ApiClientBase(http)
{

    /// <summary>Recette R-212: a page of the project's servers; the section's header filters are real column filters.</summary>
    public async Task<PaginatedResult<ProjectServerDto>> GetProjectServersPageAsync(
        int projectId, int page = 1, int pageSize = 25,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, null, sortBy, sortDescending);
        return await GetJsonAsync<PaginatedResult<ProjectServerDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/projects/{projectId}/servers", query), filters), ct).ConfigureAwait(false) ?? new();
    }


    public async Task<List<ProjectServerDto>> GetProjectServersAsync(
        int projectId, CancellationToken ct = default)
    {
        const int pageSize = 100;
        var items = new List<ProjectServerDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetProjectServersPageAsync(
                projectId, page, pageSize, ct: ct).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<ProjectServerDto?> CreateProjectServerAsync(int projectId, CreateProjectServerRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateProjectServerRequest, ProjectServerDto>($"api/projects/{projectId}/servers", request, ct);


    public Task<ProjectServerDto?> UpdateProjectServerAsync(int projectId, int projectServerId, UpdateProjectServerRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateProjectServerRequest, ProjectServerDto>($"api/projects/{projectId}/servers/{projectServerId}", request, ct);


    public Task<ApiStatus> DeleteProjectServerAsync(int projectId, int projectServerId, CancellationToken ct = default)
        => DeleteAsync($"api/projects/{projectId}/servers/{projectServerId}", ct);


    public async Task<PaginatedResult<ServerTaskDto>> GetProjectTasksAsync(
        int projectId, int page = 1, int pageSize = 25,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, null, sortBy, sortDescending);
        // Recette R-212: the section's column header filters.
        return await GetJsonAsync<PaginatedResult<ServerTaskDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/projects/{projectId}/tasks", query), filters), ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>Recette R-212: the server names the project tasks grid's Server filter offers.</summary>
    public async Task<TaskFilterValuesDto> GetProjectTaskFilterValuesAsync(int projectId, CancellationToken ct = default) =>
        await GetJsonAsync<TaskFilterValuesDto>($"api/projects/{projectId}/tasks/filter-values", ct).ConfigureAwait(false) ?? new();


    /// <summary>Recette R-212: a page of the project's logs; the section's header filters are real column filters.</summary>
    public async Task<PaginatedResult<TaskLogDto>> GetProjectLogsAsync(
        int projectId, int page = 1, int pageSize = 50,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, null, sortBy, sortDescending);
        return await GetJsonAsync<PaginatedResult<TaskLogDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/projects/{projectId}/logs", query), filters), ct).ConfigureAwait(false) ?? new();
    }


    public async Task<List<ProjectActivityDto>> GetProjectActivityAsync(int projectId, int count = 20)
    {
        return await Http.GetFromJsonAsync<List<ProjectActivityDto>>($"api/projects/{projectId}/activity?count={count}", JsonOptions.Web) ?? [];
    }

    public async Task<PaginatedResult<ProjectDto>> GetProjectsAsync(
        int page = 1,
        int pageSize = 25,
        string? search = null,
        ProjectStatus? status = null,
        string? sortBy = null,
        bool sortDescending = false,
        CancellationToken ct = default)
    {
        var query = PageQuery(page, pageSize, search);
        if (status.HasValue) query["projectStatus"] = status.Value.ToString();
        if (!string.IsNullOrEmpty(sortBy)) query["sortBy"] = sortBy;
        if (sortDescending) query["sortDescending"] = bool.TrueString;
        return await Http.GetFromJsonAsync<PaginatedResult<ProjectDto>>(
            QueryHelpers.AddQueryString("api/projects", query), JsonOptions.Web, ct) ?? new();
    }


    public async Task<ProjectDetailDto?> GetProjectDetailAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<ProjectDetailDto>($"api/projects/{id}", JsonOptions.Web, ct);
    }


    public async Task<List<ProjectDto>> GetAllProjectsAsync(
        string? search = null,
        ProjectStatus? status = null,
        string? sortBy = null,
        bool sortDescending = false,
        CancellationToken ct = default)
    {
        const int pageSize = PaginationRequest.MaxPageSize;
        var items = new List<ProjectDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetProjectsAsync(
                page, pageSize, search, status, sortBy, sortDescending, ct);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<ProjectDto?> CreateProjectAsync(CreateProjectRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateProjectRequest, ProjectDto>("api/projects", request, ct);


    public Task<ProjectDto?> UpdateProjectAsync(int id, UpdateProjectRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateProjectRequest, ProjectDto>($"api/projects/{id}", request, ct);


    public Task<ApiStatus> DeleteProjectAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/projects/{id}", ct);

    /// <summary>PLAN-013 lot 4: the project's « Conservation des données » setting.</summary>
    public Task<ProjectDataRetentionDto?> GetDataRetentionAsync(int id, CancellationToken ct = default)
        => GetJsonAsync<ProjectDataRetentionDto>($"api/projects/{id}/data-retention", ct);

    public Task<ProjectDataRetentionDto?> UpdateDataRetentionAsync(int id, UpdateProjectDataRetentionRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateProjectDataRetentionRequest, ProjectDataRetentionDto>($"api/projects/{id}/data-retention", request, ct);

    /// <summary>Recette R2-122: what deleting the project would remove, counted per kind.</summary>
    public Task<ProjectDeletionPreviewDto?> GetDeletionPreviewAsync(int id, CancellationToken ct = default)
        => GetJsonAsync<ProjectDeletionPreviewDto>($"api/projects/{id}/deletion-preview", ct);

    public async Task<PaginatedResult<ReleaseDto>> GetReleasesAsync(
        int page = 1, int pageSize = 25, int? projectId = null,
        string? sortBy = null, bool sortDescending = true, bool deployableOnly = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(),
            ["pageSize"] = pageSize.ToString()
        };
        if (deployableOnly) query["deployable"] = "true";
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            query["sortBy"] = sortBy;
            query["sortDescending"] = sortDescending.ToString();
        }
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        // Recette R-224: the releases list's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<ReleaseDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString("api/releases", query), filters), JsonOptions.Web) ?? new();
    }

    /// <summary>Recette R-224: the project and pipeline names the releases list filters offer.</summary>
    public async Task<ReleaseFilterValuesDto> GetReleaseFilterValuesAsync(int? projectId = null, CancellationToken ct = default) =>
        await GetJsonAsync<ReleaseFilterValuesDto>(
            projectId is { } id ? $"api/releases/filter-values?projectId={id}" : "api/releases/filter-values", ct).ConfigureAwait(false) ?? new();


    public async Task<ReleaseDto?> GetReleaseAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<ReleaseDto>($"api/releases/{id}", JsonOptions.Web, ct);
    }


    public async Task<List<ReleaseDto>> GetReleasesByRunAsync(int runId, CancellationToken ct = default)
    {
        try
        {
            return await Http.GetFromJsonAsync<List<ReleaseDto>>($"api/releases/by-run/{runId}", JsonOptions.Web, ct) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            // Cross-link enrichment is best-effort: a transport error or a malformed/empty body must
            // never break the run view - fall back to no linked releases.
            return [];
        }
    }


    public async Task<List<ReleaseDto>> SyncReleasesAsync(int projectId, CancellationToken ct = default)
        => await PostNoBodyAsync<List<ReleaseDto>>($"api/releases/sync/{projectId}", ct).ConfigureAwait(false) ?? [];


    public Task<ReleaseDto?> TriggerReleaseBuildAsync(int id, TriggerReleaseBuildRequest request, CancellationToken ct = default)
        => PostJsonAsync<TriggerReleaseBuildRequest, ReleaseDto>($"api/releases/{id}/build", request, ct);


    /// <summary>Recette R-366/R-367: the release's creator, later uses and build inputs.</summary>
    public async Task<ReleaseProvenanceDto?> GetReleaseProvenanceAsync(int id, CancellationToken ct = default)
        => await Http.GetFromJsonAsync<ReleaseProvenanceDto>($"api/releases/{id}/provenance", JsonOptions.Web, ct);


    public async Task<ReleaseRollbackPreviewDto?> GetRollbackPreviewAsync(int id, CancellationToken ct = default)
        => await Http.GetFromJsonAsync<ReleaseRollbackPreviewDto>($"api/releases/{id}/rollback-preview", JsonOptions.Web, ct);


    public Task<ReleaseRollbackDto?> RollbackReleaseAsync(int id, RollbackReleaseRequest request, CancellationToken ct = default)
        => PostJsonAsync<RollbackReleaseRequest, ReleaseRollbackDto>($"api/releases/{id}/rollback", request, ct);


    public async Task<ReleaseDto?> PromoteReleaseAsync(int id, CancellationToken ct = default)
        => await PostNoBodyAsync<ReleaseDto>($"api/releases/{id}/promote", ct);
}
