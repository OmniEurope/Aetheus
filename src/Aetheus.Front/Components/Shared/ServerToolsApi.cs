// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>The services managed on a server: Apache, Certbot, cron and Docker.</summary>
public sealed class ServerToolsApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<ApacheDataDto> GetApacheStateAsync(int serverId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<ApacheDataDto>($"api/servers/{serverId}/apache", JsonOptions.Web, ct) ?? new();
    }


    public async Task<List<ApacheModuleDto>> GetApacheModulesAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<ApacheModuleDto>>($"api/servers/{serverId}/apache/modules", JsonOptions.Web) ?? [];
    }


    public async Task<List<ApacheVirtualHostDto>> GetApacheVirtualHostsAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<ApacheVirtualHostDto>>($"api/servers/{serverId}/apache/vhosts", JsonOptions.Web) ?? [];
    }


    public Task<ApiStatus> ExecuteApacheActionAsync(int serverId, ApacheActionRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<ApacheActionRequest>($"api/servers/{serverId}/apache/action", request, ct);


    public Task<ApiStatus> GetApacheLogsAsync(int serverId, ApacheLogRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<ApacheLogRequest>($"api/servers/{serverId}/apache/logs", request, ct);


    public async Task<ApiStatus> GetApacheVHostConfigAsync(int serverId, string siteName, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/servers/{serverId}/apache/vhosts/{Uri.EscapeDataString(siteName)}/config", ct);
        return ApiStatus.From(response);
    }


    public Task<ApiStatus> SaveApacheVHostConfigAsync(int serverId, ApacheVHostSaveRequest request, CancellationToken ct = default)
        => PutJsonNoBodyAsync<ApacheVHostSaveRequest>($"api/servers/{serverId}/apache/vhosts/{Uri.EscapeDataString(request.SiteName)}/config", request, ct);


    public async Task<ApiStatus> GetApacheHtaccessAsync(int serverId, string documentRoot, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/servers/{serverId}/apache/htaccess?documentRoot={Uri.EscapeDataString(documentRoot)}", ct);
        return ApiStatus.From(response);
    }


    public Task<ApiStatus> SaveApacheHtaccessAsync(int serverId, ApacheHtaccessSaveRequest request, CancellationToken ct = default)
        => PutJsonNoBodyAsync<ApacheHtaccessSaveRequest>($"api/servers/{serverId}/apache/htaccess", request, ct);

    public async Task<PaginatedResult<PipelineArtifactDto>> GetProjectArtifactsAsync(
        int projectId, int page = 1, int pageSize = 25, ArtifactRetentionPolicy? policy = null, int? pipelineId = null,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null,
        string? sortBy = null, bool sortDescending = true)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(),
            ["pageSize"] = pageSize.ToString()
        };
        if (policy.HasValue) query["policy"] = ((int)policy.Value).ToString();
        if (pipelineId.HasValue) query["pipelineId"] = pipelineId.Value.ToString();
        query["sortBy"] = sortBy ?? "CreatedAt";
        query["sortDescending"] = sortDescending.ToString().ToLowerInvariant();

        // The grid's column header filters.
        var url = GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/artifacts/project/{projectId}", query), filters);
        return await Http.GetFromJsonAsync<PaginatedResult<PipelineArtifactDto>>(url, JsonOptions.Web) ?? new();
    }

    /// <summary>The pipeline and environment names the project artifacts grid's checkable filters
    /// offer.</summary>
    public async Task<ProjectArtifactFilterValuesDto> GetProjectArtifactFilterValuesAsync(int projectId, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<ProjectArtifactFilterValuesDto>(
            $"api/artifacts/project/{projectId}/filter-values", JsonOptions.Web, ct) ?? new();


    public async Task<PipelineArtifactDto?> GetArtifactAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<PipelineArtifactDto>($"api/artifacts/{id}", JsonOptions.Web, ct);
    }


    public async Task<PipelineArtifactDto?> PromoteArtifactToEnvironmentAsync(int id, string environmentName)
    {
        var request = new PromoteArtifactRequest { EnvironmentName = environmentName };
        return await PostJsonAsync<PromoteArtifactRequest, PipelineArtifactDto>($"api/artifacts/{id}/promote", request);
    }


    public async Task<Stream?> DownloadArtifactAsync(int id)
    {
        var response = await Http.GetAsync($"api/artifacts/{id}/download");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStreamAsync();
    }

    public async Task<List<CertbotCertificateDto>> GetCertbotCertificatesAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<CertbotCertificateDto>>($"api/servers/{serverId}/certbot", JsonOptions.Web) ?? [];
    }


    /// <summary>Recette R-69: the number of days before expiry the Certbot page marks a certificate "expires soon" from
    /// (the longest alert lead time); the fixed fallback when the platform cannot say.</summary>
    public async Task<int> GetCertbotExpiryWarningDaysAsync(CancellationToken ct = default) =>
        (await GetJsonAsync<CertificateExpiryWarningDto>("api/certbot/expiry-warning", ct).ConfigureAwait(false))?.Days
        ?? CertificateAlertLeadTimes.FallbackWarningDays;

    public Task<ApiStatus> ExecuteCertbotActionAsync(int serverId, CertbotActionRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<CertbotActionRequest>($"api/servers/{serverId}/certbot/action", request, ct);


    public Task<ApiStatus> CreateCertbotCertificateAsync(int serverId, CertbotCreateRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<CertbotCreateRequest>($"api/servers/{serverId}/certbot/create", request, ct);

    public Task<ApiStatus> SaveCronJobAsync(int serverId, CronJobSaveRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<CronJobSaveRequest>($"api/servers/{serverId}/cron", request, ct);


    public async Task<ApiStatus> DeleteCronJobAsync(int serverId, CronJobDeleteRequest request, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Delete, $"api/servers/{serverId}/cron")
        {
            Content = JsonContent.Create(request, options: JsonOptions.Web)
        };
        var response = await Http.SendAsync(msg, ct);
        return ApiStatus.From(response);
    }

    public async Task<List<DockerContainerDto>> GetDockerContainersAsync(int serverId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<List<DockerContainerDto>>(
            $"api/servers/{serverId}/docker/containers", JsonOptions.Web, ct) ?? [];
    }


    public Task<ApiStatus> ExecuteDockerActionAsync(int serverId, DockerActionRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<DockerActionRequest>($"api/servers/{serverId}/docker/action", request, ct);


    public async Task<string> GetContainerLogsAsync(int serverId, DockerContainerLogsRequest request, CancellationToken ct = default)
    {
        var response = await Http.PostAsJsonAsync($"api/servers/{serverId}/docker/containers/logs", request, ct);
        if (!response.IsSuccessStatusCode) return string.Empty;
        return await response.Content.ReadAsStringAsync();
    }


    public async Task<List<DockerImageDto>> GetDockerImagesAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<DockerImageDto>>($"api/servers/{serverId}/docker/images", JsonOptions.Web) ?? [];
    }


    public Task<ApiStatus> PullDockerImageAsync(int serverId, DockerPullImageRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<DockerPullImageRequest>($"api/servers/{serverId}/docker/images/pull", request, ct);


    public Task<ApiStatus> RemoveDockerImageAsync(int serverId, string imageId, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{serverId}/docker/images/{Uri.EscapeDataString(imageId)}", ct);


    public async Task<List<DockerComposeStackDto>> GetComposeStacksAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<DockerComposeStackDto>>($"api/servers/{serverId}/docker/compose", JsonOptions.Web) ?? [];
    }


    public Task<ApiStatus> ExecuteComposeActionAsync(int serverId, DockerComposeActionRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<DockerComposeActionRequest>($"api/servers/{serverId}/docker/compose/action", request, ct);


    public async Task<List<DockerNetworkDto>> GetDockerNetworksAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<DockerNetworkDto>>($"api/servers/{serverId}/docker/networks", JsonOptions.Web) ?? [];
    }


    public async Task<List<DockerVolumeDto>> GetDockerVolumesAsync(int serverId)
    {
        return await Http.GetFromJsonAsync<List<DockerVolumeDto>>($"api/servers/{serverId}/docker/volumes", JsonOptions.Web) ?? [];
    }


    public Task<ApiStatus> PruneDockerAsync(int serverId, DockerPruneRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<DockerPruneRequest>($"api/servers/{serverId}/docker/prune", request, ct);


    public Task<ApiStatus> UpdateDockerResourceLimitsAsync(int serverId, DockerResourceLimitsRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<DockerResourceLimitsRequest>($"api/servers/{serverId}/docker/resource-limits", request, ct);


    public Task<ApiStatus> InspectContainerAsync(int serverId, string containerId, CancellationToken ct = default)
        => PostNoBodyAsync($"api/servers/{serverId}/docker/containers/{Uri.EscapeDataString(containerId)}/inspect", ct);


    public async Task<ApiStatus> GetComposeFileAsync(int serverId, string stackName, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/servers/{serverId}/docker/compose/{Uri.EscapeDataString(stackName)}/file", ct);
        return ApiStatus.From(response);
    }


    public Task<ApiStatus> SaveComposeFileAsync(int serverId, DockerComposeFileSaveRequest request, CancellationToken ct = default)
        => PutJsonNoBodyAsync<DockerComposeFileSaveRequest>($"api/servers/{serverId}/docker/compose/file", request, ct);


    public Task<ApiStatus> ExecuteShellCommandAsync(int serverId, DockerExecRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<DockerExecRequest>($"api/servers/{serverId}/docker/exec", request, ct);


    public Task<ApiStatus> GetContainerEnvVarsAsync(int serverId, string containerId, CancellationToken ct = default)
        => PostNoBodyAsync($"api/servers/{serverId}/docker/containers/{Uri.EscapeDataString(containerId)}/env", ct);


    public Task<ApiStatus> ListContainerFilesAsync(int serverId, DockerBrowseRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<DockerBrowseRequest>($"api/servers/{serverId}/docker/containers/browse", request, ct);


    public Task<ApiStatus> BuildImageAsync(int serverId, DockerBuildRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<DockerBuildRequest>($"api/servers/{serverId}/docker/build", request, ct);

    public async Task<List<PortReservationDto>> GetPortReservationsAsync(int serverId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<List<PortReservationDto>>(
            $"api/servers/{serverId}/ports", JsonOptions.Web, ct) ?? [];
    }


    public Task<PortCheckResultDto?> CheckPortsAsync(
        int serverId, PortCheckRequest request, CancellationToken ct = default)
        => PostJsonAsync<PortCheckRequest, PortCheckResultDto>($"api/servers/{serverId}/ports/check", request, ct);


    /// <summary>
    /// Adds a manual reservation. The outcome carries the API error because the rejection that matters
    /// here names the current holder ("Port 10031 is already registered to 'portfolio-prod'"), and
    /// collapsing it into a null result would throw away the only useful part of the answer.
    /// </summary>
    public Task<ApiOutcome<PortReservationDto, ApiError>> CreatePortReservationAsync(
        int serverId, CreatePortReservationRequest request, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<CreatePortReservationRequest, PortReservationDto>(
            $"api/servers/{serverId}/ports", request, ct);


    public Task<ApiStatus> ReleasePortReservationAsync(int serverId, int id, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{serverId}/ports/{id}", ct);


    public Task<PortRangeDto?> GetPortRangeAsync(int serverId, CancellationToken ct = default)
        => GetJsonAsync<PortRangeDto>($"api/servers/{serverId}/ports/range", ct);


    public Task<PortRangeDto?> SetPortRangeAsync(
        int serverId, PortRangeDto range, CancellationToken ct = default)
        => PutJsonAsync<PortRangeDto, PortRangeDto>($"api/servers/{serverId}/ports/range", range, ct);


    /// <summary>
    /// Reserves free ports. The outcome carries the API error because a refusal names how many ports
    /// the window still has, which is what tells the operator to widen it.
    /// </summary>
    public Task<ApiOutcome<List<int>, ApiError>> AllocatePortsAsync(
        int serverId, AllocatePortsRequest request, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<AllocatePortsRequest, List<int>>(
            $"api/servers/{serverId}/ports/allocate", request, ct);


    /// <summary>
    /// Queues an on-demand listening-port scan on the agent. The outcome carries the API error because
    /// the refusal that matters names the missing agent capability.
    /// </summary>
    public Task<ApiOutcome<ServerTaskDto, ApiError>> ObservePortsAsync(
        int serverId, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<object, ServerTaskDto>(
            $"api/servers/{serverId}/ports/observe", new object(), ct);
}
