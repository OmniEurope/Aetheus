// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
