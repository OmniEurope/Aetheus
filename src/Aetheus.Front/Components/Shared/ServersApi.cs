// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>Servers themselves - inventory, detail, agent lifecycle - and what hangs directly off one: its configuration and its module links.</summary>
public sealed class ServersApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<PaginatedResult<ServerDto>> GetServersAsync(
        int page = 1,
        int pageSize = 25,
        string? search = null,
        ServerType? type = null,
        ServerStatus? status = null,
        string? sortBy = null,
        bool sortDescending = false,
        AgentCompatibilityStatus? compatibility = null,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = PageQuery(page, pageSize, search);
        if (type.HasValue) query["type"] = type.Value.ToString();
        if (status.HasValue) query["status"] = status.Value.ToString();
        if (compatibility.HasValue) query["compatibility"] = compatibility.Value.ToString();
        if (!string.IsNullOrEmpty(sortBy))
        {
            query["sortBy"] = sortBy;
            query["sortDescending"] = sortDescending.ToString();
        }
        // The list's column header filters.
        var url = GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/servers", query), filters);
        return await Http.GetFromJsonAsync<PaginatedResult<ServerDto>>(url, JsonOptions.Web) ?? new();
    }

    /// <summary>The OS, agent versions and tags the list's checkable filters offer.</summary>
    public async Task<ServerFilterValuesDto> GetServerFilterValuesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<ServerFilterValuesDto>("api/servers/filter-values", ct) ?? new();


    public async Task<AgentCompatibilitySummaryDto> GetAgentCompatibilitySummaryAsync(
        CancellationToken ct = default) =>
        await GetJsonAsync<AgentCompatibilitySummaryDto>(
            "api/servers/agent-compatibility-summary", ct).ConfigureAwait(false) ?? new();


    public async Task<List<string>> GetServerNamesAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<string>>("api/servers/names", ct).ConfigureAwait(false) ?? [];


    public async Task<List<ServerDto>> GetAllServersAsync()
    {
        const int pageSize = 100;
        var items = new List<ServerDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetServersAsync(page, pageSize);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<ServerDetailDto?> GetServerDetailAsync(int id, CancellationToken ct = default)
        => GetJsonAsync<ServerDetailDto>($"api/servers/{id}", ct);


    public Task<ServerDto?> UpdateServerAsync(int id, UpdateServerRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateServerRequest, ServerDto>($"api/servers/{id}", request, ct);


    /// <summary>Toggles the pipeline-runner capability on a server (item #11 secure-by-default).
    /// Returns the updated DTO or null on failure (404 / forbidden).</summary>
    public Task<ServerDto?> SetPipelineRunnerEnabledAsync(int id, bool enabled, CancellationToken ct = default)
        => PostJsonAsync<UpdatePipelineRunnerRequest, ServerDto>(
            $"api/servers/{id}/pipeline-runner",
            new UpdatePipelineRunnerRequest { Enabled = enabled },

            ct);


    /// <summary>Toggles the "containers only" isolation policy on a runner. Returns the updated
    /// DTO or null on failure (404 / forbidden).</summary>
    public Task<ServerDto?> SetContainerIsolationRequiredAsync(int id, bool required, CancellationToken ct = default)
        => PostJsonAsync<UpdateContainerIsolationPolicyRequest, ServerDto>(
            $"api/servers/{id}/container-isolation-policy",
            new UpdateContainerIsolationPolicyRequest { Required = required },

            ct);


    /// <summary>Retires the server while preserving its row and links so re-enrollment of the same
    /// machine can revive it.</summary>
    public Task<ApiStatus> RetireServerAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{id}", ct);

    public async Task<PaginatedResult<RetiredServerDto>> GetRetiredServersAsync(
        int page = 1, int pageSize = 25, string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
        // The dialog's column header filters.
        => await GetJsonAsync<PaginatedResult<RetiredServerDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString("api/servers/retired", PageQuery(page, pageSize, null, sortBy, sortDescending)), filters),
            ct).ConfigureAwait(false) ?? new();

    /// <summary>Permanently deletes a retired server, its links and its backup policies.</summary>
    public Task<ApiStatus> PurgeServerAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{id}/permanent", ct);


    /// <summary>
    /// Probes the server's agent. Returns <c>null</c> when the request itself
    /// failed (network / forbidden / not found); a non-null result with
    /// <see cref="ContactAgentResultDto.Reachable"/> false means the probe ran
    /// but the agent is not responding.
    /// </summary>
    public Task<ContactAgentResultDto?> ContactAgentAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync<ContactAgentResultDto>($"api/servers/{serverId}/contact-agent", ct);


    /// <summary>
    /// One-shot "why offline?" diagnostic - combines heartbeat freshness, agent
    /// token validity and version metadata into a single ready-to-render summary.
    /// Returns <c>null</c> when the request itself failed (network / forbidden /
    /// not found); callers should fall back to the generic error message.
    /// </summary>
    public async Task<ServerDiagnosticDto?> GetServerDiagnosticAsync(int serverId, CancellationToken ct = default)
    {
        try
        {
            return await Http.GetFromJsonAsync<ServerDiagnosticDto>($"api/servers/{serverId}/diagnostic", JsonOptions.Web, ct);
        }
        // Narrowed from a bare catch (A360-45 of the 360 audit): it swallowed OperationCanceledException,
        // so a cancelled navigation looked like "no diagnostic", and it hid deserialization faults behind
        // the same empty answer. Same shape as the rest of the API clients.
        catch (HttpRequestException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }



    /// <summary>Queues an agent self-update for one server. Null when the request itself failed.</summary>
    public Task<AgentUpdateResponse?> UpdateAgentAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync<AgentUpdateResponse>($"api/servers/{serverId}/agent/update", ct);


    /// <summary>Queues an agent self-update that also enables the named capabilities (R3-58, whitelist
    /// only). Null when the request itself failed or the backend refused it.</summary>
    public Task<AgentUpdateResponse?> EnableAgentCapabilitiesAsync(
        int serverId, IReadOnlyCollection<string> capabilities, CancellationToken ct = default)
        => PostJsonAsync<AgentEnableCapabilitiesRequest, AgentUpdateResponse>(
            $"api/servers/{serverId}/agent/enable-capabilities",
            new AgentEnableCapabilitiesRequest { Capabilities = [.. capabilities] },
            ct);


    /// <summary>Queues an agent self-update for every server the caller can administer.</summary>
    public Task<AgentUpdateAllResponse?> UpdateAllAgentsAsync(CancellationToken ct = default)
        => PostNoBodyAsync<AgentUpdateAllResponse>("api/servers/agent/update-all", ct);


    public Task<AgentUpdateAllPreviewDto?> PreviewUpdateAllAgentsAsync(CancellationToken ct = default)
        => GetJsonAsync<AgentUpdateAllPreviewDto>("api/servers/agent/update-all-preview", ct);


    public async Task<PaginatedResult<ProjectDto>> GetServerProjectsPageAsync(
        int serverId, int page, int pageSize, string? search = null, string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The projects grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<ProjectDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/servers/{serverId}/projects", query), filters), JsonOptions.Web) ?? new();
    }


    public async Task<List<ProjectDto>> GetServerProjectsAsync(int serverId)
    {
        const int pageSize = 100;
        var items = new List<ProjectDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetServerProjectsPageAsync(serverId, page, pageSize);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public async Task<List<VariableLibraryDto>> GetServerVariableLibrariesAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<VariableLibraryDto>>($"api/servers/{serverId}/variable-libraries", JsonOptions.Web) ?? [];
    }


    public async Task<List<VaultDto>> GetServerVaultsAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<VaultDto>>($"api/servers/{serverId}/vaults", JsonOptions.Web) ?? [];
    }


    /// <summary>Fetches one server-side page of releases to bound transfer independently of
    /// retention history.</summary>
    public async Task<PaginatedResult<ReleaseDto>> GetServerReleasesAsync(
        int serverId, int page = 1, int pageSize = 25,
        string? sortBy = null, bool sortDescending = true,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = PageQuery(page, pageSize, search: null, sortBy, sortDescending);
        // The releases list's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<ReleaseDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/servers/{serverId}/releases", query), filters), JsonOptions.Web) ?? new();
    }

    /// <summary>The project and pipeline names a server's releases list filters offer.</summary>
    public async Task<ReleaseFilterValuesDto> GetServerReleaseFilterValuesAsync(int serverId, CancellationToken ct = default) =>
        await GetJsonAsync<ReleaseFilterValuesDto>($"api/servers/{serverId}/releases/filter-values", ct).ConfigureAwait(false) ?? new();


    public async Task<PaginatedResult<ServerTaskDto>> GetServerTasksAsync(int serverId, int page = 1, int pageSize = 25)
    {
        return await Http.GetFromJsonAsync<PaginatedResult<ServerTaskDto>>($"api/servers/{serverId}/tasks?page={page}&pageSize={pageSize}", JsonOptions.Web) ?? new();
    }


    /// <summary>A page of the server's task log. the grid's header filters and sort travel with it,
    /// so they apply to the whole log and not to the rows already loaded.</summary>
    public async Task<PaginatedResult<TaskLogDto>> GetServerLogsAsync(
        int serverId,
        int page = 1,
        int pageSize = 50,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null,
        string? sortBy = null,
        bool sortDescending = false)
    {
        var query = PageQuery(page, pageSize, null, sortBy, sortDescending);
        var url = GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/servers/{serverId}/logs", query), filters);
        return await Http.GetFromJsonAsync<PaginatedResult<TaskLogDto>>(url, JsonOptions.Web) ?? new();
    }


    public async Task<List<ServerModuleDto>> GetServerModulesAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<ServerModuleDto>>($"api/servers/{serverId}/modules", JsonOptions.Web) ?? [];
    }


    public async Task<ServerModuleDto?> CreateServerModuleAsync(int serverId, CreateServerModuleRequest request)
    {
        var response = await Http.PostAsJsonAsync($"api/servers/{serverId}/modules", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ServerModuleDto>(JsonOptions.Web)
            : null;
    }


    public async Task<ServerModuleDto?> UpdateServerModuleAsync(int serverId, int id, UpdateServerModuleRequest request)
    {
        var response = await Http.PutAsJsonAsync($"api/servers/{serverId}/modules/{id}", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ServerModuleDto>(JsonOptions.Web)
            : null;
    }


    public async Task<ApiStatus> DeleteServerModuleAsync(int serverId, int id)
    {
        var response = await Http.DeleteAsync($"api/servers/{serverId}/modules/{id}");
        return ApiStatus.From(response);
    }


    public async Task<PaginatedResult<ServerAppDto>> GetServerAppsPageAsync(
        int serverId, int page = 1, int pageSize = 25, string? search = null,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The applications grid's column header filters.
        return await GetJsonAsync<PaginatedResult<ServerAppDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/servers/{serverId}/apps", query), filters), ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>The sources the applications grid's Source filter offers.</summary>
    public async Task<ServerAppFilterValuesDto> GetServerAppFilterValuesAsync(int serverId, CancellationToken ct = default) =>
        await GetJsonAsync<ServerAppFilterValuesDto>($"api/servers/{serverId}/apps/filter-values", ct).ConfigureAwait(false) ?? new();


    public async Task<List<ServerAppDto>> GetServerAppsAsync(
        int serverId, CancellationToken ct = default)
    {
        const int pageSize = 100;
        var items = new List<ServerAppDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetServerAppsPageAsync(
                serverId, page, pageSize, ct: ct).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public async Task<ServerAppDto?> CreateServerAppAsync(int serverId, CreateServerAppRequest request)
    {
        var response = await Http.PostAsJsonAsync($"api/servers/{serverId}/apps", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ServerAppDto>(JsonOptions.Web)
            : null;
    }


    public async Task<ServerAppDto?> UpdateServerAppAsync(int serverId, int id, UpdateServerAppRequest request)
    {
        var response = await Http.PutAsJsonAsync($"api/servers/{serverId}/apps/{id}", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ServerAppDto>(JsonOptions.Web)
            : null;
    }


    public async Task<ApiStatus> DeleteServerAppAsync(int serverId, int id)
    {
        var response = await Http.DeleteAsync($"api/servers/{serverId}/apps/{id}");
        return ApiStatus.From(response);
    }

    public async Task<string?> ExportServerConfigAsync(int serverId, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/servers/{serverId}/configuration/export", ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
    }


    public Task<ServerConfigValidationResult?> ValidateServerConfigAsync(int serverId, string yaml, CancellationToken ct = default)
        => PostJsonAsync<ServerConfigImportRequest, ServerConfigValidationResult>(
            $"api/servers/{serverId}/configuration/validate", new ServerConfigImportRequest { Yaml = yaml }, ct);


    public Task<ServerConfigPreviewDto?> PreviewServerConfigAsync(int serverId, string yaml, CancellationToken ct = default)
        => PostJsonAsync<ServerConfigImportRequest, ServerConfigPreviewDto>(
            $"api/servers/{serverId}/configuration/preview", new ServerConfigImportRequest { Yaml = yaml }, ct);


    public Task<ServerConfigDeployResultDto?> DeployServerConfigAsync(int serverId, string yaml, CancellationToken ct = default)
        => PostJsonAsync<ServerConfigImportRequest, ServerConfigDeployResultDto>(
            $"api/servers/{serverId}/configuration/deploy", new ServerConfigImportRequest { Yaml = yaml }, ct);

    public async Task<PaginatedResult<EnvironmentDto>> GetEnvironmentsAsync(
        int page = 1, int pageSize = 25, string? search = null, int? projectId = null,
        string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = PageQuery(page, pageSize, search, sortBy, sortDescending);
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        // The environments list's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<EnvironmentDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString("api/environments", query), filters), JsonOptions.Web) ?? new();
    }


    public async Task<EnvironmentDto?> GetEnvironmentAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<EnvironmentDto>($"api/environments/{id}", JsonOptions.Web, ct);
    }


    public async Task<List<EnvironmentDto>> GetAllEnvironmentsAsync(int? projectId = null)
    {
        const int pageSize = 100;
        var items = new List<EnvironmentDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetEnvironmentsAsync(page, pageSize, projectId: projectId);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<EnvironmentDto?> CreateEnvironmentAsync(CreateEnvironmentRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateEnvironmentRequest, EnvironmentDto>("api/environments", request, ct);


    public Task<EnvironmentDto?> UpdateEnvironmentAsync(int id, UpdateEnvironmentRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateEnvironmentRequest, EnvironmentDto>($"api/environments/{id}", request, ct);


    public Task<ApiStatus> DeleteEnvironmentAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/environments/{id}", ct);


    public Task<EnvironmentDto?> DuplicateEnvironmentAsync(int id, DuplicateEnvironmentRequest request, CancellationToken ct = default)
        => PostJsonAsync<DuplicateEnvironmentRequest, EnvironmentDto>($"api/environments/{id}/duplicate", request, ct);


    public async Task<ApiStatus> LinkProjectServerToEnvironmentAsync(int envId, int projectServerId, CancellationToken ct = default)
    {
        var response = await Http.PostAsync($"api/environments/{envId}/project-servers/{projectServerId}", null, ct);
        return ApiStatus.From(response);
    }


    public Task<ApiStatus> UnlinkProjectServerFromEnvironmentAsync(int envId, int projectServerId, CancellationToken ct = default)
        => DeleteAsync($"api/environments/{envId}/project-servers/{projectServerId}", ct);

    public async Task<List<ModuleLinkDto>> GetModuleLinksAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<ModuleLinkDto>>($"api/servers/{serverId}/module-links", JsonOptions.Web) ?? [];
    }


    public async Task<List<LinkedResourceDto>> GetLinksForResourceAsync(int serverId, ModuleLinkType sourceType, string sourceIdentifier)
    {
        return await Http.GetFromJsonAsync<List<LinkedResourceDto>>(
            $"api/servers/{serverId}/module-links/resource?sourceType={sourceType}&sourceIdentifier={Uri.EscapeDataString(sourceIdentifier)}", JsonOptions.Web) ?? [];
    }


    public async Task<PaginatedResult<LinkedResourceDto>> GetModuleLinksPageAsync(
        int serverId, ModuleLinkPageRequest request, CancellationToken ct = default)
    {
        return await PostJsonAsync<ModuleLinkPageRequest, PaginatedResult<LinkedResourceDto>>(
            $"api/servers/{serverId}/module-links/resource-page", request, ct)
            ?? new PaginatedResult<LinkedResourceDto>();
    }


    public Task<ModuleLinkDto?> CreateModuleLinkAsync(int serverId, CreateModuleLinkRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateModuleLinkRequest, ModuleLinkDto>($"api/servers/{serverId}/module-links", request, ct);


    public Task<ApiStatus> DeleteModuleLinkAsync(int serverId, int linkId, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{serverId}/module-links/{linkId}", ct);


    public Task<ApiStatus> AutoDetectModuleLinksAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync($"api/servers/{serverId}/module-links/auto-detect", ct);

    // S-FEAT-27: fetch the raw OpenAPI document for the in-app API Reference page.
    public async Task<JsonDocument?> GetOpenApiSpecAsync(CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync("openapi/v1.json", ct).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    public async Task<PaginatedResult<OrganizationDto>?> GetOrganizationsAsync(
        string? search, int page, int pageSize, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var qs = $"?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) qs += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(sortBy)) qs += $"&sortBy={Uri.EscapeDataString(sortBy)}&sortDescending={sortDescending.ToString().ToLowerInvariant()}";
        // The list's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<OrganizationDto>>(
            GridColumnFilters.AddTo($"api/organizations{qs}", filters), JsonOptions.Web, ct);
    }


    public async Task<OrganizationDetailDto?> GetOrganizationAsync(int id, CancellationToken ct = default)
        => await Http.GetFromJsonAsync<OrganizationDetailDto>($"api/organizations/{id}", JsonOptions.Web, ct);


    public Task<OrganizationDto?> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateOrganizationRequest, OrganizationDto>("api/organizations", request, ct);


    public Task<OrganizationDto?> UpdateOrganizationAsync(int id, UpdateOrganizationRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateOrganizationRequest, OrganizationDto>($"api/organizations/{id}", request, ct);


    public Task<ApiStatus> DeleteOrganizationAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/organizations/{id}", ct);


    public Task<OrganizationMemberDto?> AddOrganizationMemberAsync(int id, AddOrganizationMemberRequest request, CancellationToken ct = default)
        => PostJsonAsync<AddOrganizationMemberRequest, OrganizationMemberDto>($"api/organizations/{id}/members", request, ct);


    public Task<OrganizationMemberDto?> UpdateOrganizationMemberAsync(int id, int memberId, UpdateOrganizationMemberRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateOrganizationMemberRequest, OrganizationMemberDto>($"api/organizations/{id}/members/{memberId}", request, ct);


    public Task<ApiStatus> RemoveOrganizationMemberAsync(int id, int memberId, CancellationToken ct = default)
        => DeleteAsync($"api/organizations/{id}/members/{memberId}", ct);


    public async Task<List<UserOrganizationDto>> GetUserOrganizationsAsync(int userId, CancellationToken ct = default)
        => await Http.GetFromJsonAsync<List<UserOrganizationDto>>($"api/users/{userId}/organizations", JsonOptions.Web, ct) ?? [];


    public Task<ApiStatus> AssignOrganizationProjectsAsync(int id, AssignProjectsRequest request, CancellationToken ct = default)
        => PutJsonNoBodyAsync<AssignProjectsRequest>($"api/organizations/{id}/projects", request, ct);


    /// <summary>Returns the organizations the current user is a member of (header picker).</summary>
    public async Task<List<MyOrganizationDto>?> GetMyOrganizationsAsync(CancellationToken ct = default)
        => await Http.GetFromJsonAsync<List<MyOrganizationDto>>("api/organizations/me", JsonOptions.Web, ct);
}
