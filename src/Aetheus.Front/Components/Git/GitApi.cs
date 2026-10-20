// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Git;

/// <summary>The built-in Git service: repositories, commits, branches, tags, the file browser, pull requests, branch protection, the commit graph, and the external repositories mirrored into it.</summary>
public sealed class GitApi(HttpClient http) : ApiClientBase(http)
{

    public async Task<PaginatedResult<GitLightRepoDto>> GetGitReposPageAsync(
        int page = 1, int pageSize = 25, int? projectId = null, string? search = null,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(),
            ["pageSize"] = pageSize.ToString(),
            ["projectId"] = projectId?.ToString(),
            ["search"] = search,
            ["sortBy"] = sortBy,
            ["sortDescending"] = sortDescending.ToString()
        };
        // The list's column header filters.
        return await GetJsonAsync<PaginatedResult<GitLightRepoDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/git/repos", query), filters), ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>The default branches the repositories list's checkable filter offers.</summary>
    public async Task<GitRepositoryFilterValuesDto> GetGitRepoFilterValuesAsync(int? projectId = null, CancellationToken ct = default) =>
        await GetJsonAsync<GitRepositoryFilterValuesDto>(
            projectId is { } id ? $"api/git/repos/filter-values?projectId={id}" : "api/git/repos/filter-values", ct).ConfigureAwait(false) ?? new();


    // projectId null => every repo the caller can read (across accessible projects); set => that project only.
    public async Task<List<GitLightRepoDto>> GetGitReposAsync(
        int? projectId = null, CancellationToken ct = default)
    {
        const int pageSize = 100;
        var items = new List<GitLightRepoDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetGitReposPageAsync(
                page, pageSize, projectId, ct: ct).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public async Task<GitLightRepoDto?> GetGitRepoAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<GitLightRepoDto>($"api/git/repos/{id}", JsonOptions.Web, ct);
    }


    public Task<GitLightRepoDto?> CreateGitRepoAsync(CreateGitLightRepoRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateGitLightRepoRequest, GitLightRepoDto>("api/git/repos", request, ct);


    public Task<GitLightRepoDto?> UpdateGitRepoAsync(int id, UpdateGitLightRepoRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateGitLightRepoRequest, GitLightRepoDto>($"api/git/repos/{id}", request, ct);


    public Task<ApiStatus> DeleteGitRepoAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/git/repos/{id}", ct);


    public async Task<PaginatedResult<GitLightCommitDto>> GetGitCommitsAsync(int repoId, string? refName = null, int page = 1, int pageSize = 30, string? search = null,
        IReadOnlyList<GridFilter>? filters = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(),
            ["pageSize"] = pageSize.ToString()
        };
        if (!string.IsNullOrEmpty(refName)) query["ref"] = refName;
        if (!string.IsNullOrEmpty(search)) query["search"] = search;
        return await Http.GetFromJsonAsync<PaginatedResult<GitLightCommitDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/git/repos/{repoId}/commits", query), filters), JsonOptions.Web) ?? new();
    }


    /// <summary>The repository at <paramref name="refName"/> (its default branch when null) as a
    /// zip, with the file name the API offers; null when the API refused it.</summary>
    public async Task<(Stream Content, string FileName)?> DownloadGitArchiveAsync(int repoId, string? refName = null, CancellationToken ct = default)
    {
        var url = $"api/git/repos/{repoId}/archive";
        if (!string.IsNullOrEmpty(refName)) url = QueryHelpers.AddQueryString(url, "ref", refName);
        var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        return (await response.Content.ReadAsStreamAsync(ct), string.IsNullOrWhiteSpace(fileName) ? $"repository-{repoId}.zip" : fileName);
    }

    public async Task<GitLightCommitDetailDto?> GetGitCommitDetailAsync(int repoId, string sha, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/git/repos/{repoId}/commits/{Uri.EscapeDataString(sha)}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitLightCommitDetailDto>(JsonOptions.Web, ct);
    }


    public async Task<Dictionary<string, string>> GetGitCommitMessagesAsync(
        int repoId, IReadOnlyCollection<string> shas, CancellationToken ct = default)
        => await PostJsonAsync<GitCommitMessagesRequest, Dictionary<string, string>>(
            $"api/git/repos/{repoId}/commit-messages", new GitCommitMessagesRequest { Shas = [.. shas] }, ct)

            .ConfigureAwait(false) ?? new(StringComparer.OrdinalIgnoreCase);


    public async Task<PaginatedResult<GitLightBranchDto>> GetGitBranchesPageAsync(
        int repoId, int page = 1, int pageSize = 25, string? search = null,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        return await GetJsonAsync<PaginatedResult<GitLightBranchDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/git/repos/{repoId}/branches", query), filters), ct).ConfigureAwait(false) ?? new();
    }


    public async Task<List<GitLightBranchDto>> GetGitBranchesAsync(
        int repoId, CancellationToken ct = default)
    {
        const int pageSize = 100;
        var items = new List<GitLightBranchDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetGitBranchesPageAsync(repoId, page, pageSize, ct: ct).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<ApiStatus> CreateGitBranchAsync(int repoId, CreateGitLightBranchRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<CreateGitLightBranchRequest>($"api/git/repos/{repoId}/branches", request, ct);


    public Task<ApiStatus> DeleteGitBranchAsync(int repoId, string name, CancellationToken ct = default)
        => DeleteAsync($"api/git/repos/{repoId}/branches/{Uri.EscapeDataString(name)}", ct);


    public async Task<PaginatedResult<GitLightTagDto>> GetGitTagsPageAsync(
        int repoId, int page = 1, int pageSize = 25, string? search = null,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        return await GetJsonAsync<PaginatedResult<GitLightTagDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/git/repos/{repoId}/tags", query), filters), ct).ConfigureAwait(false) ?? new();
    }


    public async Task<List<GitLightTagDto>> GetGitTagsAsync(
        int repoId, CancellationToken ct = default)
    {
        const int pageSize = 100;
        var items = new List<GitLightTagDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetGitTagsPageAsync(repoId, page, pageSize, ct: ct).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<ApiStatus> CreateGitTagAsync(int repoId, CreateGitLightTagRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<CreateGitLightTagRequest>($"api/git/repos/{repoId}/tags", request, ct);


    public Task<ApiStatus> DeleteGitTagAsync(int repoId, string name, CancellationToken ct = default)
        => DeleteAsync($"api/git/repos/{repoId}/tags/{Uri.EscapeDataString(name)}", ct);


    public async Task<PaginatedResult<GitLightTreeEntryDto>> GetGitTreePageAsync(
        int repoId, string? refName = null, string? path = null,
        int page = 1, int pageSize = 25, string? search = null,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        if (!string.IsNullOrEmpty(refName)) query["ref"] = refName;
        if (!string.IsNullOrEmpty(path)) query["path"] = path;
        return await GetJsonAsync<PaginatedResult<GitLightTreeEntryDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/git/repos/{repoId}/tree", query), filters), ct).ConfigureAwait(false) ?? new();
    }


    public async Task<List<GitLightTreeEntryDto>> GetGitTreeAsync(
        int repoId, string? refName = null, string? path = null, CancellationToken ct = default)
    {
        const int pageSize = 100;
        var items = new List<GitLightTreeEntryDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetGitTreePageAsync(
                repoId, refName, path, page, pageSize, ct: ct).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public async Task<GitLightBlobDto?> GetGitBlobAsync(int repoId, string refName, string path, CancellationToken ct = default)
    {
        var url = QueryHelpers.AddQueryString($"api/git/repos/{repoId}/blob",
            new Dictionary<string, string?> { ["ref"] = refName, ["path"] = path });
        return await Http.GetFromJsonAsync<GitLightBlobDto>(url, JsonOptions.Web, ct);
    }


    public async Task<List<GitLightBlameLine>> GetGitBlameAsync(int repoId, string refName, string path)
    {
        var url = QueryHelpers.AddQueryString($"api/git/repos/{repoId}/blame",
            new Dictionary<string, string?> { ["ref"] = refName, ["path"] = path });
        return await Http.GetFromJsonAsync<List<GitLightBlameLine>>(url, JsonOptions.Web) ?? [];
    }


    public async Task<PaginatedResult<InternalPullRequestDto>> GetGitPullRequestsAsync(
        int repoId, int page = 1, int pageSize = 25, string? search = null, PullRequestStatus? status = null,
        string? sortBy = null, bool sortDescending = true, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = PageQuery(page, pageSize, search, sortBy, sortDescending);
        if (status.HasValue) query["status"] = status.Value.ToString();
        return await Http.GetFromJsonAsync<PaginatedResult<InternalPullRequestDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/git/repos/{repoId}/pull-requests", query), filters), JsonOptions.Web) ?? new();
    }

    /// <summary>The authors the commits and pull requests grids' Author filters offer.</summary>
    public async Task<GitRepositoryDetailFilterValuesDto> GetGitRepoDetailFilterValuesAsync(int repoId, CancellationToken ct = default) =>
        await GetJsonAsync<GitRepositoryDetailFilterValuesDto>($"api/git/repos/{repoId}/filter-values", ct).ConfigureAwait(false) ?? new();


    public async Task<InternalPullRequestDto?> GetGitPullRequestAsync(int repoId, int prNumber, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<InternalPullRequestDto>($"api/git/repos/{repoId}/pull-requests/{prNumber}", JsonOptions.Web, ct);
    }


    public Task<InternalPullRequestDto?> CreateGitPullRequestAsync(int repoId, CreateInternalPullRequestRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateInternalPullRequestRequest, InternalPullRequestDto>($"api/git/repos/{repoId}/pull-requests", request, ct);


    public Task<InternalPullRequestDto?> MergeGitPullRequestAsync(int repoId, int prNumber, CancellationToken ct = default)
        => PostNoBodyAsync<InternalPullRequestDto>($"api/git/repos/{repoId}/pull-requests/{prNumber}/merge", ct);


    public Task<InternalPullRequestDto?> CloseGitPullRequestAsync(int repoId, int prNumber, CancellationToken ct = default)
        => PostNoBodyAsync<InternalPullRequestDto>($"api/git/repos/{repoId}/pull-requests/{prNumber}/close", ct);


    public async Task<PullRequestDiffDto?> GetGitPullRequestDiffAsync(int repoId, int prNumber, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<PullRequestDiffDto>($"api/git/repos/{repoId}/pull-requests/{prNumber}/diff", JsonOptions.Web, ct);
    }


    public async Task<string> GetGitCommitGraphAsync(int repoId, int maxCount = 100, CancellationToken ct = default)
    {
        return await Http.GetStringAsync($"api/git/repos/{repoId}/graph?maxCount={maxCount}");
    }


    public async Task<List<GitLightCommitDto>> GetGitGraphDataAsync(int repoId, int maxCount = 120, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<List<GitLightCommitDto>>(
            $"api/git/repos/{repoId}/graph-data?maxCount={maxCount}", JsonOptions.Web, ct) ?? [];
    }


    public async Task<PaginatedResult<BranchProtectionRuleDto>> GetGitBranchProtectionRulesPageAsync(
        int repoId, int page = 1, int pageSize = 25, string? search = null,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        return await GetJsonAsync<PaginatedResult<BranchProtectionRuleDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/git/repos/{repoId}/branch-protection", query), filters), ct).ConfigureAwait(false) ?? new();
    }


    public async Task<List<BranchProtectionRuleDto>> GetGitBranchProtectionRulesAsync(
        int repoId, CancellationToken ct = default)
    {
        const int pageSize = 100;
        var items = new List<BranchProtectionRuleDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetGitBranchProtectionRulesPageAsync(
                repoId, page, pageSize, ct: ct).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<BranchProtectionRuleDto?> CreateGitBranchProtectionRuleAsync(int repoId, CreateBranchProtectionRuleRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateBranchProtectionRuleRequest, BranchProtectionRuleDto>($"api/git/repos/{repoId}/branch-protection", request, ct);


    public Task<BranchProtectionRuleDto?> UpdateGitBranchProtectionRuleAsync(int repoId, int ruleId, UpdateBranchProtectionRuleRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateBranchProtectionRuleRequest, BranchProtectionRuleDto>($"api/git/repos/{repoId}/branch-protection/{ruleId}", request, ct);


    public Task<ApiStatus> DeleteGitBranchProtectionRuleAsync(int repoId, int ruleId, CancellationToken ct = default)
        => DeleteAsync($"api/git/repos/{repoId}/branch-protection/{ruleId}", ct);

    public async Task<GitCommitDto?> GetGitCommitAsync(int id, CancellationToken ct = default)
        => await Http.GetFromJsonAsync<GitCommitDto>($"api/gitgraph/commits/{id}", JsonOptions.Web, ct);


    public async Task<GitBranchDto?> GetGitBranchAsync(int id, CancellationToken ct = default)
        => await Http.GetFromJsonAsync<GitBranchDto>($"api/gitgraph/branches/{id}", JsonOptions.Web, ct);



    public async Task<ExternalRepoDto?> GetExternalRepoAsync(int projectId)
    {
        var response = await Http.GetAsync($"api/external-repos/project/{projectId}");
        // An absent external repository is normal: 204 is expected and 404 is accepted for
        // compatibility.
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ExternalRepoDto>(JsonOptions.Web);
    }


    public Task<ExternalRepoDto?> AttachExternalRepoAsync(AttachExternalRepoRequest request, CancellationToken ct = default)
        => PostJsonAsync<AttachExternalRepoRequest, ExternalRepoDto>("api/external-repos/attach", request, ct);


    public async Task<ExternalRepoDto?> SyncExternalRepoNowAsync(int projectId)
    {
        var response = await Http.PostAsync($"api/external-repos/project/{projectId}/sync", null);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<ExternalRepoDto>(JsonOptions.Web);
    }


    public Task<ApiStatus> DetachExternalRepoAsync(int projectId, CancellationToken ct = default)
        => DeleteAsync($"api/external-repos/project/{projectId}", ct);
}
