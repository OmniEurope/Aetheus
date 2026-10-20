// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>Maps analysis portfolio column filters and the sort columns not handled by the typed
/// switch, using grid column keys.</summary>
internal static class AnalysisPortfolioQuery
{
    internal static readonly GridQueryMap<AnalysisReport> Columns = new GridQueryMap<AnalysisReport>()
        .Number("pipelineRunId", report => report.PipelineRunId)
        .Text("organizationName", report => report.Organization.Name)
        .Text("projectName", report => report.Project.Name)
        .Text("pipelineName", report => report.PipelineRun == null ? null : report.PipelineRun.Pipeline.Name)
        .Enum("category", report => report.Category)
        .Text("scannerName", report => report.ScannerName)
        .Enum("gateStatus", report => report.Evaluation == null ? (AnalysisGateStatus?)null : report.Evaluation.Status)
        .Enum("grade", report => report.Evaluation == null ? null : report.Evaluation.Grade)
        .Number("findingCount", report => report.Occurrences.Select(occurrence => occurrence.AnalysisFindingId).Distinct().Count())
        .Number("newFindingCount", report => report.Occurrences.Count(occurrence => occurrence.IsNew))
        .Text("branchName", report => report.BranchName)
        .Text("commitHash", report => report.CommitHash)
        .Date("completedAt", report => report.CompletedAt);

    /// <summary>The caller's scope, the typed filters an older front still sends, the column filters and
    /// the search, in that order and all before the count.</summary>
    internal static IQueryable<AnalysisReport> ApplyPortfolioFilters(
        IQueryable<AnalysisReport> query,
        IReadOnlyCollection<int>? accessibleProjectIds,
        AnalysisPortfolioPaginationRequest request)
    {
        if (accessibleProjectIds is not null)
        {
            var projectIds = accessibleProjectIds.ToArray();
            query = query.Where(report => projectIds.Contains(report.ProjectId));
        }
        if (request.OrganizationId.HasValue)
            query = query.Where(report => report.OrganizationId == request.OrganizationId.Value);
        if (request.ProjectId.HasValue)
            query = query.Where(report => report.ProjectId == request.ProjectId.Value);
        if (request.PipelineId.HasValue)
            query = query.Where(report => report.PipelineRun != null
                && report.PipelineRun.PipelineId == request.PipelineId.Value);
        if (request.Category.HasValue)
            query = query.Where(report => report.Category == request.Category.Value);
        if (!string.IsNullOrWhiteSpace(request.Branch))
            query = query.Where(report => report.BranchName == request.Branch.Trim());
        if (!string.IsNullOrWhiteSpace(request.Commit))
        {
            var commit = request.Commit.Trim().ToLowerInvariant();
            query = query.Where(report => (report.CommitHash ?? string.Empty).ToLower().StartsWith(commit));
        }
        if (request.From.HasValue) query = query.Where(report => report.CompletedAt >= request.From.Value);
        if (request.To.HasValue) query = query.Where(report => report.CompletedAt <= request.To.Value);
        // The grid's column header filters, after the scope and before the count.
        query = Columns.ApplyFilters(query, request.Filters);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim().ToLowerInvariant();
            query = query.Where(report => report.Project.Name.ToLower().Contains(search)
                || report.Organization.Name.ToLower().Contains(search)
                || report.ScannerName.ToLower().Contains(search)
                || (report.BranchName ?? string.Empty).ToLower().Contains(search)
                 || (report.CommitHash ?? string.Empty).ToLower().Contains(search)
                 || (report.PipelineRun != null ? report.PipelineRun.Pipeline.Name : string.Empty).ToLower().Contains(search));
        }
        return query;
    }

    internal static IQueryable<AnalysisReport> OrderPortfolio(
        IQueryable<AnalysisReport> query,
        AnalysisPortfolioPaginationRequest request) =>
        (request.SortBy, request.SortDescending) switch
        {
            ("OrganizationName", false) => query.OrderBy(report => report.Organization.Name),
            ("OrganizationName", true) => query.OrderByDescending(report => report.Organization.Name),
            ("ProjectName", false) => query.OrderBy(report => report.Project.Name),
            ("ProjectName", true) => query.OrderByDescending(report => report.Project.Name),
            ("PipelineName", false) => query.OrderBy(report => report.PipelineRun == null ? null : report.PipelineRun.Pipeline.Name),
            ("PipelineName", true) => query.OrderByDescending(report => report.PipelineRun == null ? null : report.PipelineRun.Pipeline.Name),
            ("Category", false) => query.OrderBy(report => report.Category),
            ("Category", true) => query.OrderByDescending(report => report.Category),
            ("Status", false) => query.OrderBy(report => report.Status),
            ("Status", true) => query.OrderByDescending(report => report.Status),
            ("CompletedAt", false) => query.OrderBy(report => report.CompletedAt),
            ("CompletedAt", true) => query.OrderByDescending(report => report.CompletedAt),
            // The other grid columns (scanner, gate, grade, counts...) sort through the column map
            // instead of silently falling back to the newest first.
            ({ Length: > 0 } key, var descending) when Columns.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)
                => Columns.ApplySorts(query, [new GridSort { Field = key, Descending = descending }])!,
            _ => query.OrderByDescending(report => report.CompletedAt)
        };
}
