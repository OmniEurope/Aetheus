// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.DependencyUpdates;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>Code-analysis reads: findings, metrics, components, policies and their exceptions, plus the coverage and complexity trends a run produces.</summary>
public sealed class AnalysisApi(HttpClient http) : ApiClientBase(http)
{

    public async Task<PipelineCoverageSummaryDto?> GetCoverageSummaryAsync(int runId, CancellationToken ct = default)
    {
        try { return await Http.GetFromJsonAsync<PipelineCoverageSummaryDto>($"api/pipelines/runs/{runId}/coverage", JsonOptions.Web, ct); }
        catch (HttpRequestException) { return null; }
    }


    public async Task<List<CoverageTrendPointDto>> GetCoverageTrendAsync(int runId, int take = 15, CancellationToken ct = default)
    {
        try { return await Http.GetFromJsonAsync<List<CoverageTrendPointDto>>($"api/pipelines/runs/{runId}/coverage-trend?take={take}", JsonOptions.Web, ct) ?? []; }
        catch (HttpRequestException) { return []; }
    }


    public async Task<List<ComplexityTrendPointDto>> GetComplexityTrendAsync(int runId, int take = 15, CancellationToken ct = default)
    {
        try { return await Http.GetFromJsonAsync<List<ComplexityTrendPointDto>>($"api/pipelines/runs/{runId}/metrics-trend?take={take}", JsonOptions.Web, ct) ?? []; }
        catch (HttpRequestException) { return []; }
    }


    public async Task<ProjectQualityTrendDto> GetProjectQualityTrendAsync(int projectId, int take = 15, CancellationToken ct = default)
    {
        try { return await Http.GetFromJsonAsync<ProjectQualityTrendDto>($"api/pipelines/projects/{projectId}/quality-trend?take={take}", JsonOptions.Web, ct) ?? new(); }
        catch (HttpRequestException) { return new(); }
    }


    public async Task<AnalysisProjectSummaryDto> GetAnalysisSummaryAsync(int projectId, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<AnalysisProjectSummaryDto>($"api/analysis/projects/{projectId}/summary", JsonOptions.Web, ct) ?? new();


    public async Task<List<AnalysisPortfolioProjectDto>> GetAnalysisPortfolioProjectsAsync(
        CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<List<AnalysisPortfolioProjectDto>>(
            "api/analysis/portfolio/projects", JsonOptions.Web, ct) ?? [];


    /// <summary>R-15: one page of the advisories the caller's projects are concerned by, one entry per CVE with its
    /// projects. Throws <see cref="HttpRequestException"/> on a failed read.</summary>
    public async Task<PaginatedResult<CveWatchAdvisoryGroupDto>> GetCveWatchAdvisoriesAsync(
        int page, int pageSize, AnalysisSeverity? severity = null, bool exploitedOnly = false, bool withoutFixOnly = false,
        bool includeClosed = false, int? projectId = null, int? organizationId = null, CancellationToken ct = default)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(invariant),
            ["pageSize"] = pageSize.ToString(invariant)
        };
        if (severity.HasValue) query["severity"] = severity.Value.ToString();
        if (exploitedOnly) query["exploitedOnly"] = "true";
        if (withoutFixOnly) query["withoutFixOnly"] = "true";
        if (includeClosed) query["includeClosed"] = "true";
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString(invariant);
        if (organizationId.HasValue) query["organizationId"] = organizationId.Value.ToString(invariant);
        return await Http.GetFromJsonAsync<PaginatedResult<CveWatchAdvisoryGroupDto>>(
            QueryHelpers.AddQueryString("api/analysis/cve-watch/advisories", query), JsonOptions.Web, ct) ?? new();
    }

    /// <summary>R-15: the figures of the CVE watch tab, the uncovered projects and the freshness of the sources.</summary>
    public async Task<CveWatchOverviewDto> GetCveWatchOverviewAsync(CancellationToken ct = default) =>
        await GetJsonAsync<CveWatchOverviewDto>("api/analysis/cve-watch/overview", ct) ?? new();

    /// <summary>R-4: every feed of the CVE watch with its state, and the licences owed. The site administrator's; throws on a failed read.</summary>
    public async Task<VulnerabilitySourcesDto> GetVulnerabilitySourcesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<VulnerabilitySourcesDto>("api/vulnerability-watch/sources", ct)
        ?? throw new HttpRequestException("The vulnerability sources answer was empty.");

    /// <summary>
    /// R-4: asks the feed service for a pass now. The server answers 202 when it takes the request and 409 with the
    /// reason otherwise; both carry the outcome, so a 409 is an answer, not a failure (no global toast). Throws
    /// <see cref="HttpRequestException"/> on any other status.
    /// </summary>
    public async Task<VulnerabilitySyncRequestDto> RequestVulnerabilitySyncAsync(CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/vulnerability-watch/sync");
        message.Options.Set(ErrorNotificationHandler.CallerHandlesConflict, true);
        using var response = await Http.SendAsync(message, ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.Conflict))
            response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VulnerabilitySyncRequestDto>(JsonOptions.Web, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("The pass request answer was empty.");
    }

    /// <summary>Moves the one daily pass of the CVE watch (a drag in the planner). Site administrators only: the endpoint refuses any other role.</summary>
    public Task<ApiStatus> SetVulnerabilityPassTimeAsync(SetVulnerabilityPassTimeRequest request, CancellationToken ct = default) =>
        PutJsonNoBodyAsync("api/vulnerability-watch/pass-time", request, ct);

    /// <summary>PLAN-007: the automatic dependency updates of a project and its setting. Throws <see cref="HttpRequestException"/> on a failed read.</summary>
    public async Task<DependencyUpdatesOverviewDto> GetDependencyUpdatesAsync(int projectId, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<DependencyUpdatesOverviewDto>($"api/projects/{projectId}/dependency-updates", JsonOptions.Web, ct)
        ?? throw new HttpRequestException("The dependency updates answer was empty.");

    /// <summary>PLAN-007: turns the automatic dependency updates of a project on or off. Throws <see cref="HttpRequestException"/> on a refusal.</summary>
    public async Task SetDependencyUpdatesEnabledAsync(int projectId, bool enabled, CancellationToken ct = default)
    {
        using var response = await Http.PutAsJsonAsync(
            $"api/projects/{projectId}/dependency-updates/settings", new UpdateDependencyUpdateSettingsRequest { Enabled = enabled }, JsonOptions.Web, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>What the CVE watch says of one project (open CVE by severity, inventories, last pass), null when it is unknown or not readable.
    /// Throws <see cref="HttpRequestException"/> on a failed read; the caller shows its own failure, no global toast.</summary>
    public Task<CveWatchProjectDto?> GetCveWatchProjectAsync(int projectId, CancellationToken ct = default) =>
        ReadOrNullAsync<CveWatchProjectDto>($"api/analysis/cve-watch/projects/{projectId}", ct);

    /// <summary>The CVE watch match an analysis finding was written for, null when the finding has none (or it is not readable).</summary>
    public Task<CveWatchMatchDto?> GetCveWatchMatchByFindingAsync(int findingId, CancellationToken ct = default) =>
        ReadOrNullAsync<CveWatchMatchDto>($"api/analysis/cve-watch/by-finding/{findingId}", ct);

    /// <summary>The latest attempt to fix a match, null when the match is unknown or not readable.</summary>
    public Task<CveRemediationDto?> GetCveRemediationAsync(long matchId, CancellationToken ct = default) =>
        ReadOrNullAsync<CveRemediationDto>($"api/analysis/cve-watch/{matchId}/remediation", ct);

    /// <summary>
    /// Asks the platform to prepare the fix of a match. 201 (started) and 200 (already under way) are answers, so is 422: the platform
    /// cannot do it and the answer carries the command to run and why, shown in place, never as a global toast. Any other status throws
    /// <see cref="HttpRequestException"/>.
    /// </summary>
    public async Task<CveRemediationRequestResult> RequestCveRemediationAsync(long matchId, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"api/analysis/cve-watch/{matchId}/remediation");
        message.Options.Set(ErrorNotificationHandler.CallerHandlesErrors, true);
        using var response = await Http.SendAsync(message, ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity))
            response.EnsureSuccessStatusCode();
        var remediation = await response.Content.ReadFromJsonAsync<CveRemediationDto>(JsonOptions.Web, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("The remediation answer was empty.");
        return new CveRemediationRequestResult(
            remediation, response.StatusCode == HttpStatusCode.UnprocessableEntity, response.StatusCode == HttpStatusCode.OK);
    }

    private async Task<T?> ReadOrNullAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Options.Set(ErrorNotificationHandler.CallerHandlesErrors, true);
        using var response = await Http.SendAsync(message, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions.Web, ct).ConfigureAwait(false);
    }

    /// <summary>R-22: one run of the CVE watch, null when it is unknown or not readable.</summary>
    public async Task<CveWatchRunDto?> GetCveWatchRunAsync(int reportId, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"api/analysis/cve-watch/runs/{reportId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CveWatchRunDto>(JsonOptions.Web, ct);
    }


    public async Task<AnalysisRunGateDto?> GetAnalysisRunGateAsync(int runId, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<AnalysisRunGateDto>($"api/analysis/runs/{runId}/result", JsonOptions.Web, ct);

    /// <summary>One page of the findings a run and the runs it triggered observed, with the counts
    /// of the whole set. Throws <see cref="HttpRequestException"/> on a failed read.</summary>
    public async Task<AnalysisRunFindingsPageDto> GetAnalysisRunFindingsAsync(
        AnalysisRunFindingsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = PageQuery(request.Page, request.PageSize, request.Search, request.SortBy, request.SortDescending);
        if (request.IncludeDecided) query["includeDecided"] = "true";
        if (request.IsNew.HasValue) query["isNew"] = request.IsNew.Value.ToString();
        AddIdRange(query, request.IdFrom, request.IdTo, request.IdNot);
        var url = QueryHelpers.AddQueryString("api/analysis/runs/findings", query);
        url = QueryValues.AddMany(url, "runIds", request.RunIds);
        url = QueryValues.AddMany(url, "categories", request.Categories);
        url = QueryValues.AddMany(url, "severities", request.Severities);
        url = QueryValues.AddMany(url, "statuses", request.Statuses);
        return await Http.GetFromJsonAsync<AnalysisRunFindingsPageDto>(url, JsonOptions.Web, ct) ?? new();
    }

    /// <summary>The ID column's number filter, as the findings routes read it.</summary>
    private static void AddIdRange(Dictionary<string, string?> query, int? idFrom, int? idTo, int? idNot)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (idFrom.HasValue) query["idFrom"] = idFrom.Value.ToString(invariant);
        if (idTo.HasValue) query["idTo"] = idTo.Value.ToString(invariant);
        if (idNot.HasValue) query["idNot"] = idNot.Value.ToString(invariant);
    }


    public async Task<PaginatedResult<AnalysisPortfolioRowDto>> GetAnalysisPortfolioAsync(
        int page, int pageSize, string? search = null, int? organizationId = null, int? projectId = null,
        int? pipelineId = null, AnalysisCategory? category = null, string? branch = null, string? commit = null,
        DateTime? from = null, DateTime? to = null, string? sortBy = null, bool sortDescending = false,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = PageQuery(page, pageSize, search, sortBy, sortDescending);
        if (organizationId.HasValue) query["organizationId"] = organizationId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (pipelineId.HasValue) query["pipelineId"] = pipelineId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (category.HasValue) query["category"] = category.Value.ToString();
        if (!string.IsNullOrWhiteSpace(branch)) query["branch"] = branch;
        if (!string.IsNullOrWhiteSpace(commit)) query["commit"] = commit;
        if (from.HasValue) query["from"] = from.Value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (to.HasValue) query["to"] = to.Value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        // The grid's column header filters.
        var url = GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/analysis/portfolio", query), filters);
        return await Http.GetFromJsonAsync<PaginatedResult<AnalysisPortfolioRowDto>>(url, JsonOptions.Web, ct) ?? new();
    }

    /// <summary>The organizations, projects and scanners the portfolio's checkable filters
    /// offer.</summary>
    public async Task<AnalysisPortfolioFilterValuesDto> GetAnalysisPortfolioFilterValuesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<AnalysisPortfolioFilterValuesDto>("api/analysis/portfolio/filter-values", ct) ?? new();


    public async Task<PaginatedResult<AnalysisFindingDto>> GetAnalysisFindingsAsync(
        int projectId, int page, int pageSize, string? search = null, AnalysisCategory? category = null,
        AnalysisSeverity? severity = null, AnalysisFindingStatus? status = null, bool? isNew = null,
        string? scanner = null, string? branch = null, string? responsible = null,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyCollection<AnalysisCategory>? categories = null,
        IReadOnlyCollection<AnalysisSeverity>? severities = null,
        IReadOnlyCollection<AnalysisFindingStatus>? statuses = null,
        int? idFrom = null, int? idTo = null, int? idNot = null)
    {
        var query = PageQuery(page, pageSize, search, sortBy, sortDescending);
        AddIdRange(query, idFrom, idTo, idNot);
        if (category.HasValue) query["category"] = category.Value.ToString();
        if (severity.HasValue) query["severity"] = severity.Value.ToString();
        if (status.HasValue) query["status"] = status.Value.ToString();
        if (isNew.HasValue) query["isNew"] = isNew.Value.ToString();
        if (!string.IsNullOrWhiteSpace(scanner)) query["scanner"] = scanner;
        if (!string.IsNullOrWhiteSpace(branch)) query["branch"] = branch;
        if (!string.IsNullOrWhiteSpace(responsible)) query["responsible"] = responsible;
        var url = QueryHelpers.AddQueryString($"api/analysis/projects/{projectId}/findings", query);
        // A multi-valued column filter repeats its key, one value each.
        url = QueryValues.AddMany(url, "categories", categories);
        url = QueryValues.AddMany(url, "severities", severities);
        url = QueryValues.AddMany(url, "statuses", statuses);
        return await Http.GetFromJsonAsync<PaginatedResult<AnalysisFindingDto>>(url, JsonOptions.Web, ct) ?? new();
    }


    public async Task<AnalysisFindingDto?> GetAnalysisFindingAsync(int findingId, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<AnalysisFindingDto>($"api/analysis/findings/{findingId}", JsonOptions.Web, ct);


    public async Task<List<AnalysisFindingOccurrenceDto>> GetAnalysisFindingOccurrencesAsync(
        int findingId, int take = 100, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<List<AnalysisFindingOccurrenceDto>>(
            $"api/analysis/findings/{findingId}/occurrences?take={Math.Clamp(take, 1, 200)}", JsonOptions.Web, ct) ?? [];


    public async Task<List<AnalysisFindingDecisionDto>> GetAnalysisFindingDecisionsAsync(
        int findingId, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<List<AnalysisFindingDecisionDto>>(
            $"api/analysis/findings/{findingId}/decisions", JsonOptions.Web, ct) ?? [];


    public Task<ApiOutcome<AnalysisFindingDecisionDto, ApiError>> CreateAnalysisFindingDecisionAsync(
        int findingId, CreateAnalysisFindingDecisionRequest request, CancellationToken ct = default) =>
        PostForApiErrorOutcomeAsync<CreateAnalysisFindingDecisionRequest, AnalysisFindingDecisionDto>(
            $"api/analysis/findings/{findingId}/decisions", request, ct);

    /// <summary>Reverts the active decision of the finding, which is open again.</summary>
    public Task<ApiStatus> RevokeAnalysisFindingDecisionAsync(int findingId, CancellationToken ct = default) =>
        DeleteAsync($"api/analysis/findings/{findingId}/decisions/active", ct);


    public async Task<PaginatedResult<AnalysisMetricDto>> GetAnalysisMetricsAsync(
        int projectId, int page, int pageSize, string? key = null, string? language = null,
        string? branch = null, string? sortBy = null, bool sortDescending = false,
        bool latestReportOnly = false, CancellationToken ct = default)
    {
        var query = PageQuery(page, pageSize, null, sortBy, sortDescending);
        if (!string.IsNullOrWhiteSpace(key)) query["key"] = key;
        if (!string.IsNullOrWhiteSpace(language)) query["language"] = language;
        if (!string.IsNullOrWhiteSpace(branch)) query["branch"] = branch;
        if (latestReportOnly) query["latestReportOnly"] = bool.TrueString;
        return await Http.GetFromJsonAsync<PaginatedResult<AnalysisMetricDto>>(
            QueryHelpers.AddQueryString($"api/analysis/projects/{projectId}/metrics", query), JsonOptions.Web, ct) ?? new();
    }


    public async Task<PaginatedResult<AnalysisComponentDto>> GetAnalysisComponentsAsync(
        int projectId, int page, int pageSize, string? search = null, string? componentType = null,
        string? branch = null, string? sortBy = null, bool sortDescending = false, CancellationToken ct = default)
    {
        var query = PageQuery(page, pageSize, search, sortBy, sortDescending);
        if (!string.IsNullOrWhiteSpace(componentType)) query["componentType"] = componentType;
        if (!string.IsNullOrWhiteSpace(branch)) query["branch"] = branch;
        return await Http.GetFromJsonAsync<PaginatedResult<AnalysisComponentDto>>(
            QueryHelpers.AddQueryString($"api/analysis/projects/{projectId}/components", query), JsonOptions.Web, ct) ?? new();
    }


    public async Task<PaginatedResult<AnalysisReportDto>> GetAnalysisReportsAsync(
        int projectId, int page, int pageSize, string? search = null, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<PaginatedResult<AnalysisReportDto>>(
            QueryHelpers.AddQueryString($"api/analysis/projects/{projectId}/reports",
                PageQuery(page, pageSize, search, sortBy, sortDescending)), JsonOptions.Web, ct) ?? new();


    public async Task<List<AnalysisPolicyDto>> GetAnalysisPoliciesAsync(int projectId, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<List<AnalysisPolicyDto>>($"api/analysis/projects/{projectId}/policies", JsonOptions.Web, ct) ?? [];


    public Task<AnalysisPolicyDto?> CreateAnalysisPolicyAsync(
        int projectId, UpsertAnalysisPolicyRequest request, CancellationToken ct = default) =>
        PostJsonAsync<UpsertAnalysisPolicyRequest, AnalysisPolicyDto>(
            $"api/analysis/projects/{projectId}/policies", request, ct);


    public Task<AnalysisPolicyDto?> UpdateAnalysisPolicyAsync(
        int projectId, int policyId, UpsertAnalysisPolicyRequest request, CancellationToken ct = default) =>
        PutJsonAsync<UpsertAnalysisPolicyRequest, AnalysisPolicyDto>(
             $"api/analysis/projects/{projectId}/policies/{policyId}", request, ct);


    public Task<List<AnalysisPolicyDto>?> ApplyAnalysisPolicyBatchAsync(
        int projectId,
        ApplyAnalysisPolicyBatchRequest request,
        CancellationToken ct = default) =>
        PostJsonAsync<ApplyAnalysisPolicyBatchRequest, List<AnalysisPolicyDto>>(
            $"api/analysis/projects/{projectId}/policies/batch", request, ct);


    public async Task<List<AnalysisPolicyDto>> GetGlobalAnalysisPoliciesAsync(CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<List<AnalysisPolicyDto>>(
            "api/analysis/policies/global", JsonOptions.Web, ct) ?? [];


    public Task<AnalysisPolicyDto?> CreateGlobalAnalysisPolicyAsync(
        UpsertAnalysisPolicyRequest request,
        CancellationToken ct = default) =>
        PostJsonAsync<UpsertAnalysisPolicyRequest, AnalysisPolicyDto>(
            "api/analysis/policies/global", request, ct);


    public Task<AnalysisPolicyDto?> UpdateGlobalAnalysisPolicyAsync(
        int policyId,
        UpsertAnalysisPolicyRequest request,
        CancellationToken ct = default) =>
        PutJsonAsync<UpsertAnalysisPolicyRequest, AnalysisPolicyDto>(
            $"api/analysis/policies/global/{policyId}", request, ct);


    public Task<List<AnalysisPolicyDto>?> ApplyGlobalAnalysisPolicyBatchAsync(
        ApplyAnalysisPolicyBatchRequest request,
        CancellationToken ct = default) =>
        PostJsonAsync<ApplyAnalysisPolicyBatchRequest, List<AnalysisPolicyDto>>(
            "api/analysis/policies/global/batch", request, ct);


    public async Task<List<AnalysisPolicyDto>> GetOrganizationAnalysisPoliciesAsync(
        int organizationId,
        CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<List<AnalysisPolicyDto>>(
            $"api/analysis/organizations/{organizationId}/policies", JsonOptions.Web, ct) ?? [];


    public Task<AnalysisPolicyDto?> CreateOrganizationAnalysisPolicyAsync(
        int organizationId,
        UpsertAnalysisPolicyRequest request,
        CancellationToken ct = default) =>
        PostJsonAsync<UpsertAnalysisPolicyRequest, AnalysisPolicyDto>(
            $"api/analysis/organizations/{organizationId}/policies", request, ct);


    public Task<AnalysisPolicyDto?> UpdateOrganizationAnalysisPolicyAsync(
        int organizationId,
        int policyId,
        UpsertAnalysisPolicyRequest request,
        CancellationToken ct = default) =>
        PutJsonAsync<UpsertAnalysisPolicyRequest, AnalysisPolicyDto>(
            $"api/analysis/organizations/{organizationId}/policies/{policyId}", request, ct);


    public Task<List<AnalysisPolicyDto>?> ApplyOrganizationAnalysisPolicyBatchAsync(
        int organizationId,
        ApplyAnalysisPolicyBatchRequest request,
        CancellationToken ct = default) =>
        PostJsonAsync<ApplyAnalysisPolicyBatchRequest, List<AnalysisPolicyDto>>(
            $"api/analysis/organizations/{organizationId}/policies/batch", request, ct);


    public Task<AnalysisPolicySetPreviewDto?> PreviewAnalysisPolicySetAsync(
        AnalysisPolicyScope scope,
        int? organizationId,
        int? projectId,
        PreviewAnalysisPolicySetRequest request,
        CancellationToken ct = default)
    {
        var path = scope switch
        {
            AnalysisPolicyScope.Global => "api/analysis/policies/global/preview",
            AnalysisPolicyScope.Organization =>
                $"api/analysis/organizations/{organizationId}/policies/preview",
            AnalysisPolicyScope.Project => $"api/analysis/projects/{projectId}/policies/preview",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
        return PostJsonAsync<PreviewAnalysisPolicySetRequest, AnalysisPolicySetPreviewDto>(
            path,
            request,
            ct);
    }


    public async Task<List<AnalysisPolicyRevisionDto>> GetAnalysisPolicyRevisionsAsync(
        AnalysisPolicyScope scope,
        int? organizationId,
        int? projectId,
        int policyId,
        CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<List<AnalysisPolicyRevisionDto>>(
            $"{AnalysisPolicyScopePath(scope, organizationId, projectId)}/{policyId}/revisions",
            JsonOptions.Web,
            ct) ?? [];


    public async Task<AnalysisPolicyDto?> RollbackAnalysisPolicyAsync(
        AnalysisPolicyScope scope,
        int? organizationId,
        int? projectId,
        int policyId,
        int version,
        CancellationToken ct = default)
    {
        using var response = await Http.PostAsync(
            $"{AnalysisPolicyScopePath(scope, organizationId, projectId)}/{policyId}/rollback/{version}",
            null,
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AnalysisPolicyDto>(JsonOptions.Web, ct)
            .ConfigureAwait(false);
    }


    private static string AnalysisPolicyScopePath(
        AnalysisPolicyScope scope,
        int? organizationId,
        int? projectId) => scope switch
        {
            AnalysisPolicyScope.Global => "api/analysis/policies/global",
            AnalysisPolicyScope.Organization => $"api/analysis/organizations/{organizationId}/policies",
            AnalysisPolicyScope.Project => $"api/analysis/projects/{projectId}/policies",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };


    public async Task<List<AnalysisPolicyExceptionDto>> GetAnalysisExceptionsAsync(int projectId, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<List<AnalysisPolicyExceptionDto>>($"api/analysis/projects/{projectId}/exceptions", JsonOptions.Web, ct) ?? [];


    public Task<AnalysisPolicyExceptionDto?> CreateAnalysisExceptionAsync(
        int projectId, CreateAnalysisPolicyExceptionRequest request, CancellationToken ct = default) =>
        PostJsonAsync<CreateAnalysisPolicyExceptionRequest, AnalysisPolicyExceptionDto>(
            $"api/analysis/projects/{projectId}/exceptions", request, ct);


    public Task<ApiStatus> RevokeAnalysisExceptionAsync(
        int projectId, int exceptionId, CancellationToken ct = default) =>
        DeleteAsync($"api/analysis/projects/{projectId}/exceptions/{exceptionId}", ct);


    public async Task<AnalysisTrackingStatusDto?> GetAnalysisTrackingStatusAsync(int projectId, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"api/analysis/projects/{projectId}/tracking", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AnalysisTrackingStatusDto>(JsonOptions.Web, ct).ConfigureAwait(false);
    }


    public async Task<PaginatedResult<AnalysisVulnerabilityObservationDto>> GetAnalysisVulnerabilitiesAsync(
        int projectId, int page, int pageSize, string? search = null, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default) =>
        await Http.GetFromJsonAsync<PaginatedResult<AnalysisVulnerabilityObservationDto>>(
            QueryHelpers.AddQueryString($"api/analysis/projects/{projectId}/vulnerabilities",
                PageQuery(page, pageSize, search, sortBy, sortDescending)), JsonOptions.Web, ct) ?? new();
}
