// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Pipelines, their runs and the tasks and work items attached to them.</summary>
public sealed class PipelinesApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<PaginatedResult<ServerTaskDto>> GetTasksAsync(
        int page = 1, int pageSize = 25, int? serverId = null,
        string? sortBy = null, bool sortDescending = true,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        // Name and Status filtering belongs to the grid's column filters.
        var query = PageQuery(page, pageSize, null, sortBy, sortDescending);
        if (serverId.HasValue) query["serverId"] = serverId.Value.ToString();
        // The list's column header filters.
        var url = GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/tasks", query), filters);
        return await Http.GetFromJsonAsync<PaginatedResult<ServerTaskDto>>(url, JsonOptions.Web) ?? new();
    }

    /// <summary>The server names the task list's Server filter offers (one server's when
    /// set).</summary>
    public async Task<TaskFilterValuesDto> GetTaskFilterValuesAsync(int? serverId = null, CancellationToken ct = default) =>
        await GetJsonAsync<TaskFilterValuesDto>(
            serverId is { } id ? $"api/tasks/filter-values?serverId={id}" : "api/tasks/filter-values", ct).ConfigureAwait(false) ?? new();


    public async Task<ServerTaskDto?> GetTaskAsync(int taskId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<ServerTaskDto>($"api/tasks/{taskId}", JsonOptions.Web, ct);
    }


    public async Task<ServerTaskDto?> CreateTaskAsync(CreateTaskRequest request)
    {
        var response = await Http.PostAsJsonAsync("api/tasks", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ServerTaskDto>(JsonOptions.Web)
            : null;
    }


    public Task<ApiStatus> CancelTaskAsync(int taskId, CancellationToken ct = default)
        => PostNoBodyAsync($"api/tasks/{taskId}/cancel", ct);


    /// <summary>
    /// Item #7: in-flight tasks the caller can see, used by the top-bar tracker on (re)connect.
    /// Already RBAC-filtered server-side.
    /// </summary>
    public async Task<List<ServerTaskDto>> GetActiveTasksAsync(CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<List<ServerTaskDto>>("api/tasks/active", JsonOptions.Web, ct) ?? [];
    }

    public async Task<PaginatedResult<PipelineDto>> GetPipelinesAsync(int page = 1, int pageSize = 25, string? search = null, PipelineTriggerType? triggerType = null, int? environmentId = null, int? projectServerId = null, int? projectId = null)
    {
        var query = PageQuery(page, pageSize, search);
        if (triggerType.HasValue) query["triggerType"] = triggerType.Value.ToString();
        if (environmentId.HasValue) query["environmentId"] = environmentId.Value.ToString();
        if (projectServerId.HasValue) query["projectServerId"] = projectServerId.Value.ToString();
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        return await Http.GetFromJsonAsync<PaginatedResult<PipelineDto>>(
            QueryHelpers.AddQueryString("api/pipelines", query), JsonOptions.Web) ?? new();
    }


    public async Task<PipelineDependencyGroupsDto> GetPipelineDependencyGroupsAsync(
        int? serverId = null, int? projectId = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (serverId is not null) query.Add($"serverId={serverId}");
        if (projectId is not null) query.Add($"projectId={projectId}");
        var uri = query.Count > 0
            ? $"api/pipelines/dependencies?{string.Join("&", query)}"
            : "api/pipelines/dependencies";
        return await Http.GetFromJsonAsync<PipelineDependencyGroupsDto>(uri, JsonOptions.Web, ct) ?? new();
    }


    public Task<PipelineDependencyGroupsDto> GetPipelineDependencyGroupsAsync(CancellationToken ct) =>
        GetPipelineDependencyGroupsAsync(serverId: null, projectId: null, ct: ct);


    public async Task<PipelineFavoritesDto> GetPipelineFavoritesAsync(CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<PipelineFavoritesDto>(
            "api/pipelines/favorites", JsonOptions.Web, ct) ?? new PipelineFavoritesDto();


    public Task<PipelineFavoriteDto?> SetPipelineFavoriteAsync(
        int pipelineId,
        bool isFavorite,
        CancellationToken ct = default) =>
        PutJsonAsync<SetPipelineFavoriteRequest, PipelineFavoriteDto>(
            $"api/pipelines/{pipelineId}/favorite",
            new SetPipelineFavoriteRequest { IsFavorite = isFavorite },

            ct);


    public async Task<List<PipelineRunDto>> GetActivePipelineRunsAsync(int? projectId = null, CancellationToken ct = default)
    {
        var uri = projectId.HasValue
            ? $"api/pipelines/runs/active?projectId={projectId.Value}"
            : "api/pipelines/runs/active";
        return await Http.GetFromJsonAsync<List<PipelineRunDto>>(uri, JsonOptions.Web, ct) ?? [];
    }


    public async Task<List<PipelineRunDto>> GetRecentPipelineRunsAsync(
        int? projectId = null, int? serverId = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>();
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        if (serverId.HasValue) query["serverId"] = serverId.Value.ToString();
        var uri = QueryHelpers.AddQueryString("api/pipelines/runs/recent", query);
        return await Http.GetFromJsonAsync<List<PipelineRunDto>>(uri, JsonOptions.Web, ct) ?? [];
    }


    public Task<List<PipelineRunDto>> GetRecentPipelineRunsAsync(int? projectId, CancellationToken ct) =>
        GetRecentPipelineRunsAsync(projectId, serverId: null, ct);


    public async Task<PaginatedResult<PipelineDependencyDto>> GetPipelineDependencyPageAsync(
        int page, int pageSize, string? search = null, PipelineTriggerType? triggerType = null,
        int? projectId = null, int? serverId = null, CancellationToken ct = default)
    {
        var query = PageQuery(page, pageSize, search);
        if (triggerType.HasValue) query["triggerType"] = triggerType.Value.ToString();
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        if (serverId.HasValue) query["serverId"] = serverId.Value.ToString();
        var uri = QueryHelpers.AddQueryString("api/pipelines/dependencies/page", query);
        using var response = await Http.GetAsync(uri, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Rolling-deployment compatibility: an older backend only exposes the unpaged graph.
            var legacy = await GetPipelineDependencyGroupsAsync(ct: ct);
            var serverPipelineIds = serverId.HasValue
                ? (await GetServerPipelinesAsync(serverId.Value)).Select(item => item.Id).ToHashSet()
                : null;
            return PageLegacyDependencies(legacy, page, pageSize, search, triggerType, projectId, serverPipelineIds);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PaginatedResult<PipelineDependencyDto>>(JsonOptions.Web, ct) ?? new();
    }

    /// <summary>The page the paged endpoint would have answered, worked out from the whole graph an older
    /// backend returns: same filters, ordered by name.</summary>
    internal static PaginatedResult<PipelineDependencyDto> PageLegacyDependencies(
        PipelineDependencyGroupsDto legacy, int page, int pageSize, string? search,
        PipelineTriggerType? triggerType, int? projectId, IReadOnlySet<int>? serverPipelineIds)
    {
        var filtered = legacy.Parents.Concat(legacy.Leaves)
            .Where(item => !projectId.HasValue || item.ProjectId == projectId)
            .Where(item => serverPipelineIds is null || serverPipelineIds.Contains(item.Id))
            .Where(item => string.IsNullOrWhiteSpace(search)
                || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (item.ProjectName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(item => !triggerType.HasValue || item.TriggerType == triggerType)
            .OrderBy(item => item.Name).ToList();
        return new PaginatedResult<PipelineDependencyDto>
        {
            Items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            TotalCount = filtered.Count,
            Page = page,
            PageSize = pageSize
        };
    }


    public async Task<PipelineDto?> GetPipelineAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<PipelineDto>($"api/pipelines/{id}", JsonOptions.Web, ct);
    }


    public async Task<PipelineSourceDto?> GetPipelineSourceAsync(int id, CancellationToken ct = default)
        => (await LookUpPipelineSourceAsync(id, ct)).Source;

    /// <summary> The pipeline's Git source, telling "the backend answered that there is none" (the
    /// definition is kept in Aetheus) apart from "it could not be read" (Loaded false), so the page
    /// never claims a storage place it did not read. </summary>
    public async Task<(bool Loaded, PipelineSourceDto? Source)> LookUpPipelineSourceAsync(int id, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/pipelines/{id}/source", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return (true, null);
        if (!response.IsSuccessStatusCode) return (false, null);
        return (true, await response.Content.ReadFromJsonAsync<PipelineSourceDto>(JsonOptions.Web, ct));
    }


    public Task<ApiOutcome<PipelineDto, YamlValidationResultDto>> CreatePipelineAsync(
        CreatePipelineRequest request, CancellationToken ct = default)
        => PostForOutcomeAsync<PipelineDto, YamlValidationResultDto>("api/pipelines", request, ct);


    /// <summary>What would stop the wizard's pipelines from running, checked before they are created.</summary>
    public Task<PipelineSetupReadinessDto?> CheckSetupReadinessAsync(
        int projectId, IReadOnlyList<string> templateNames, CancellationToken ct = default)
        => PostJsonAsync<PipelineSetupReadinessRequest, PipelineSetupReadinessDto>(
            "api/pipelines/setup/readiness",
            new PipelineSetupReadinessRequest { ProjectId = projectId, TemplateNames = [.. templateNames] },
            ct);

    /// <summary>What copying a project's pipelines into another one would do (names
    /// only).</summary>
    public Task<PipelineProjectCopyPlanDto?> PlanProjectCopyAsync(PipelineProjectCopyRequest request, CancellationToken ct = default)
        => PostJsonAsync<PipelineProjectCopyRequest, PipelineProjectCopyPlanDto>("api/pipelines/setup/copy-plan", request, ct);

    /// <summary>Makes the copies; the error payload is kept so the wizard can show why it
    /// failed.</summary>
    public Task<ApiOutcome<PipelineProjectCopyResultDto, ApiError>> CopyProjectPipelinesAsync(
        PipelineProjectCopyRequest request, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<PipelineProjectCopyRequest, PipelineProjectCopyResultDto>("api/pipelines/setup/copy", request, ct);

    /// <summary>What would stop the project's existing pipelines from running.</summary>
    public Task<PipelineSetupReadinessDto?> GetProjectReadinessAsync(int projectId, CancellationToken ct = default)
        => Http.GetFromJsonAsync<PipelineSetupReadinessDto>(
            $"api/pipelines/setup/project-readiness?projectId={projectId}", JsonOptions.Web, ct);

    /// <summary>The same verdict for one pipeline, asked before it is launched.</summary>
    public Task<PipelineSetupReadinessDto?> GetPipelineReadinessAsync(int pipelineId, CancellationToken ct = default)
        => Http.GetFromJsonAsync<PipelineSetupReadinessDto>(
            $"api/pipelines/setup/pipeline-readiness?pipelineId={pipelineId}", JsonOptions.Web, ct);

    /// <summary>Usual stage and step durations are drawn from successful runs started before this
    /// run.</summary>
    public Task<RunStageBaselinesDto?> GetStageBaselinesAsync(int pipelineId, int runId, CancellationToken ct = default)
        => Http.GetFromJsonAsync<RunStageBaselinesDto>(
            $"api/pipelines/{pipelineId}/runs/{runId}/stage-baselines", JsonOptions.Web, ct);

    /// <summary>Libraries and vaults the project's pipelines require and it lacks.</summary>
    public async Task<List<UnmetRequirementDto>> GetUnmetRequirementsAsync(int projectId, CancellationToken ct = default)
        => await Http.GetFromJsonAsync<List<UnmetRequirementDto>>(
            $"api/pipelines/setup/unmet?projectId={projectId}", JsonOptions.Web, ct) ?? [];

    /// <summary>Creates what the project's existing pipelines are waiting for.</summary>
    public Task<PipelineRequirementsProvisionResultDto?> ProvisionUnmetRequirementsAsync(int projectId, CancellationToken ct = default)
        => PostJsonAsync<object, PipelineRequirementsProvisionResultDto>(
            $"api/pipelines/setup/provision-unmet?projectId={projectId}", new { }, ct);

    /// <summary>Creates the libraries and vaults required by the templates with keys only and empty
    /// values.</summary>
    public Task<PipelineRequirementsProvisionResultDto?> ProvisionSetupRequirementsAsync(
        int projectId, IReadOnlyList<string> templateNames, CancellationToken ct = default)
        => PostJsonAsync<PipelineSetupReadinessRequest, PipelineRequirementsProvisionResultDto>(
            "api/pipelines/setup/provision",
            new PipelineSetupReadinessRequest { ProjectId = projectId, TemplateNames = [.. templateNames] },
            ct);


    public Task<ApiOutcome<PipelineDto, YamlValidationResultDto>> UpdatePipelineAsync(
        int id, UpdatePipelineRequest request, CancellationToken ct = default)
        => PutForOutcomeAsync<PipelineDto, YamlValidationResultDto>($"api/pipelines/{id}", request, ct);


    public Task<ApiStatus> DeletePipelineAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/pipelines/{id}", ct);


    // Optional queue-time parameters and a per-run source-branch override (validated server-side).
    public Task<ApiOutcome<PipelineRunDto, YamlValidationResultDto>> TriggerPipelineRunAsync(
        int id, Dictionary<string, string>? parameters = null, string? sourceBranch = null, CancellationToken ct = default,
        bool runToEnd = false)
    {
        // Expand-contract wire shape: old backends expect a top-level dictionary. New backends split the
        // reserved branch key back into PipelineRunRequest.SourceBranch, so both deployment colours work.
        var body = parameters is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(sourceBranch))
            body["AETHEUS_RUN_BRANCH"] = sourceBranch;
        body["AETHEUS_RUN_IDEMPOTENCY_KEY"] = Guid.NewGuid().ToString("N");
        // Sent only when chosen, and only offered when GetRunOptionsAsync said the backend accepts it.
        if (runToEnd)
            body["AETHEUS_RUN_TO_END"] = "true";
        return PostForOutcomeAsync<PipelineRunDto, YamlValidationResultDto>(
            $"api/pipelines/{id}/run", body, ct);
    }


    /// <summary>The launch options this instance offers for a manual run ("run to end").</summary>
    public Task<PipelineRunOptionsDto?> GetRunOptionsAsync(CancellationToken ct = default)
        => GetJsonAsync<PipelineRunOptionsDto>("api/pipelines/run-options", ct);


    // P: the queue-time parameters declared by the pipeline's authoritative (git-first) YAML.
    public async Task<List<PipelineRunParameterDto>> GetPipelineRunParametersAsync(int id, string? sourceBranch = null, CancellationToken ct = default)
    {
        var uri = $"api/pipelines/{id}/parameters";
        if (!string.IsNullOrWhiteSpace(sourceBranch)) uri += $"?sourceBranch={Uri.EscapeDataString(sourceBranch)}";
        return await Http.GetFromJsonAsync<List<PipelineRunParameterDto>>(uri, JsonOptions.Web, ct) ?? [];
    }


    public Task<ApiOutcome<PipelinePreflightDto, YamlValidationResultDto>> PreflightPipelineAsync(
        int id, string? sourceBranch = null, CancellationToken ct = default)
    {
        var uri = $"api/pipelines/{id}/preflight";
        if (!string.IsNullOrWhiteSpace(sourceBranch))
            uri += $"?sourceBranch={Uri.EscapeDataString(sourceBranch)}";
        return PostForOutcomeAsync<PipelinePreflightDto, YamlValidationResultDto>(uri, null, ct);
    }


    public Task<PipelineRunDto?> RetryFailedStepsAsync(int runId, CancellationToken ct = default)
        => PostNoBodyAsync<PipelineRunDto>($"api/pipelines/runs/{runId}/retry-failed", ct);


    // G: re-launch a run - current definition, snapshot @ same commit, or snapshot @ branch head.
    public Task<PipelineRunDto?> RerunPipelineRunAsync(int runId, RerunMode mode, CancellationToken ct = default)
        => PostNoBodyAsync<PipelineRunDto>($"api/pipelines/runs/{runId}/rerun?mode={mode}", ct);


    public Task<PipelineCheckpointResumePreviewDto?> GetCheckpointResumePreviewAsync(int runId, CancellationToken ct = default)
        => GetJsonAsync<PipelineCheckpointResumePreviewDto>($"api/pipelines/runs/{runId}/checkpoint-resume-preview", ct);


    // Cancel an in-progress run (204 on success; 404 when the run is already terminal / unknown).
    public Task<ApiStatus> CancelPipelineRunAsync(int runId, CancellationToken ct = default)
        => PostNoBodyAsync($"api/pipelines/runs/{runId}/cancel", ct);


    public Task<DryRunResultDto?> DryRunPipelineAsync(int id, Dictionary<string, string>? additionalVars = null, CancellationToken ct = default)
        => PostJsonAsync<Dictionary<string, string>?, DryRunResultDto>($"api/pipelines/{id}/dry-run", additionalVars, ct);


    public async Task<PaginatedResult<PipelineFleetItemDto>> GetPipelineFleetAsync(
        int page, int pageSize, string? search = null, int? templateId = null,
        int? projectId = null, PipelineFleetFreshness? freshness = null,
        string? sortBy = null, bool sortDescending = false,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (templateId is not null) query.Add($"templateId={templateId}");
        if (projectId is not null) query.Add($"projectId={projectId}");
        if (freshness is not null) query.Add($"freshness={freshness}");
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            query.Add($"sortBy={Uri.EscapeDataString(sortBy)}");
            query.Add($"sortDescending={sortDescending.ToString().ToLowerInvariant()}");
        }
        // The fleet grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<PipelineFleetItemDto>>(
            GridColumnFilters.AddTo($"api/pipelines/fleet?{string.Join('&', query)}", filters), JsonOptions.Web, ct)
            ?? new PaginatedResult<PipelineFleetItemDto>();
    }

    /// <summary>The template names the fleet's Template column filter offers.</summary>
    public async Task<PipelineFleetFilterValuesDto> GetPipelineFleetFilterValuesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<PipelineFleetFilterValuesDto>("api/pipelines/fleet/filter-values", ct).ConfigureAwait(false) ?? new();


    public Task<PipelineFleetItemDto?> GetPipelineFleetItemAsync(
        int pipelineId, CancellationToken ct = default) =>
        Http.GetFromJsonAsync<PipelineFleetItemDto>(
            $"api/pipelines/{pipelineId}/fleet-item", JsonOptions.Web, ct);


    public Task<ApiOutcome<PipelineFleetUpdatePreviewDto, ApiError>> PreviewPipelineFleetUpdateAsync(
        int pipelineId, int targetVersion, CancellationToken ct = default) =>
        PostForApiErrorOutcomeAsync<PipelineFleetUpdateRequest, PipelineFleetUpdatePreviewDto>(
            $"api/pipelines/{pipelineId}/fleet-update/preview",
            new PipelineFleetUpdateRequest { TargetVersion = targetVersion }, ct);


    public Task<PipelineDto?> ApplyPipelineFleetUpdateAsync(
        int pipelineId, PipelineFleetUpdateRequest request, CancellationToken ct = default) =>
        PostJsonAsync<PipelineFleetUpdateRequest, PipelineDto>(
            $"api/pipelines/{pipelineId}/fleet-update", request, ct);


    public Task<PipelinePromotePreviewDto?> GetPipelinePromotePreviewAsync(
        int pipelineId, CancellationToken ct = default) =>
        Http.GetFromJsonAsync<PipelinePromotePreviewDto>(
            $"api/pipelines/{pipelineId}/promote-template/preview", JsonOptions.Web, ct);


    public async Task<List<PipelineRunDto>> GetPipelineRunsAsync(int pipelineId)
    {
        // F12: backend now returns PaginatedResult; default to first page (server clamps).
        var paged = await Http.GetFromJsonAsync<PaginatedResult<PipelineRunDto>>($"api/pipelines/{pipelineId}/runs?pageSize=50", JsonOptions.Web);
        return paged?.Items ?? [];
    }


    public async Task<PaginatedResult<PipelineRunDto>> GetPipelineRunsPagedAsync(
        int pipelineId, int page = 1, int pageSize = 25, PipelineRunPaginationRequest? request = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(),
            ["pageSize"] = pageSize.ToString()
        };
        if (!string.IsNullOrWhiteSpace(request?.SortBy))
        {
            query["sortBy"] = request.SortBy;
            query["sortDescending"] = request.SortDescending ? "true" : "false";
        }
        if (request?.Status is { } status) query["status"] = status.ToString();
        if (!string.IsNullOrWhiteSpace(request?.BranchName)) query["branchName"] = request.BranchName;
        if (!string.IsNullOrWhiteSpace(request?.CommitHash)) query["commitHash"] = request.CommitHash;

        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<PipelineRunDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/pipelines/{pipelineId}/runs", query), request?.Filters),
            JsonOptions.Web)
            ?? new PaginatedResult<PipelineRunDto>();
    }


    public async Task<PipelineRunDto?> GetPipelineRunAsync(int runId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<PipelineRunDto>($"api/pipelines/runs/{runId}", JsonOptions.Web, ct);
    }


    /// <summary>Who launched the run and which runs it started.</summary>
    public async Task<PipelineRunLineageDto?> GetPipelineRunLineageAsync(int runId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<PipelineRunLineageDto>($"api/pipelines/runs/{runId}/lineage", JsonOptions.Web, ct);
    }

    /// <summary>The expand/contract gate verdicts the run's migrate steps reported (recette R1-43).</summary>
    public async Task<List<PipelineRunMigrationGateDto>> GetPipelineRunMigrationGateAsync(int runId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<List<PipelineRunMigrationGateDto>>($"api/pipelines/runs/{runId}/migration-gate", JsonOptions.Web, ct) ?? [];
    }

    public async Task<PipelineRunQueueStateDto?> GetPipelineRunQueueAsync(int runId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<PipelineRunQueueStateDto>(
            $"api/pipelines/runs/{runId}/queue", JsonOptions.Web, ct);
    }

    public async Task<List<WorkItemBoardColumn>> GetWorkItemBoardAsync(int projectId)
    {
        return await Http.GetFromJsonAsync<List<WorkItemBoardColumn>>($"api/work-items/board/{projectId}", JsonOptions.Web) ?? [];
    }


    public Task<WorkItemDto?> CreateWorkItemAsync(CreateWorkItemRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateWorkItemRequest, WorkItemDto>("api/work-items", request, ct);


    public Task<WorkItemDto?> UpdateWorkItemAsync(int id, UpdateWorkItemRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateWorkItemRequest, WorkItemDto>($"api/work-items/{id}", request, ct);


    public async Task<WorkItemDto?> MoveWorkItemAsync(int id, MoveWorkItemRequest request)
    {
        var response = await Http.PatchAsJsonAsync($"api/work-items/{id}/move", request, JsonOptions.Web);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions.Web);
    }


    public Task<ApiStatus> DeleteWorkItemAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/work-items/{id}", ct);

    /// <summary>
    /// Routed under <c>api/servers</c> but a pipelines read, and it lives here because the legacy
    /// fallback in <see cref="GetPipelineDependencyPageAsync"/> needs it: a sub-client reaching into
    /// another one would tie back together exactly what the split separated.
    /// </summary>
    public async Task<List<PipelineDto>> GetServerPipelinesAsync(int serverId)
        => await Http.GetFromJsonAsync<List<PipelineDto>>($"api/servers/{serverId}/pipelines", JsonOptions.Web) ?? [];

    /// <summary>F2: approvals raised on a run (see <c>PipelineStageDispatchPlanner.CheckAndCreateApprovalAsync</c>).
    /// A run in <see cref="PipelineStatus.WaitingForApproval"/> has exactly one Pending entry here.</summary>
    public async Task<List<PipelineApprovalDto>> GetApprovalsAsync(int runId, CancellationToken ct = default)
        => await Http.GetFromJsonAsync<List<PipelineApprovalDto>>(
            $"api/pipelines/runs/{runId}/approvals", JsonOptions.Web, ct) ?? [];

    /// <summary>Every approval still waiting, across the pipelines the user may read.</summary>
    public async Task<List<PendingApprovalDto>> GetPendingApprovalsAsync(CancellationToken ct = default)
        => await Http.GetFromJsonAsync<List<PendingApprovalDto>>(
            "api/pipelines/approvals/pending", JsonOptions.Web, ct) ?? [];

    /// <summary>A refused decision keeps the reason the backend gives (already decided, run ended,
    /// run not moved), so the page can say why instead of one generic failure.</summary>
    public Task<ApiOutcome<PipelineApprovalDto, ApiError>> DecideApprovalAsync(
        int approvalId, ApprovalDecisionRequest request, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<ApprovalDecisionRequest, PipelineApprovalDto>(
            $"api/pipelines/approvals/{approvalId}/decide", request, ct);

    /// <summary>The pipelines a user owns (administrators only).</summary>
    public async Task<List<PipelineOwnershipDto>> GetOwnedPipelinesAsync(string owner, CancellationToken ct = default)
        => await Http.GetFromJsonAsync<List<PipelineOwnershipDto>>(
            QueryHelpers.AddQueryString("api/pipelines/ownership/owned", "owner", owner), JsonOptions.Web, ct) ?? [];

    /// <summary>The active users a pipeline can be given to.</summary>
    public async Task<List<PipelineOwnerCandidateDto>> GetOwnerCandidatesAsync(CancellationToken ct = default)
        => await Http.GetFromJsonAsync<List<PipelineOwnerCandidateDto>>(
            "api/pipelines/ownership/candidates", JsonOptions.Web, ct) ?? [];

    /// <summary>What an owner transfer would do, with the Server.Admin warnings, without
    /// writing.</summary>
    public Task<ApiOutcome<PipelineOwnerTransferResultDto, ApiError>> PreviewOwnerTransferAsync(
        PipelineOwnerTransferRequest request, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<PipelineOwnerTransferRequest, PipelineOwnerTransferResultDto>(
            "api/pipelines/ownership/preview", request, ct);

    /// <summary>Changes the owner of the pipelines of the request's scope.</summary>
    public Task<ApiOutcome<PipelineOwnerTransferResultDto, ApiError>> TransferOwnerAsync(
        PipelineOwnerTransferRequest request, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<PipelineOwnerTransferRequest, PipelineOwnerTransferResultDto>(
            "api/pipelines/ownership/transfer", request, ct);
}
