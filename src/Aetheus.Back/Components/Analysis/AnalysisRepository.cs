// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

public sealed class AnalysisRepository(AppDbContext db) : IAnalysisRepository
{
    private readonly AnalysisReportRepository _reports = new(db);
    private readonly AnalysisInsightsRepository _insights = new(db);
    private readonly AnalysisGovernanceRepository _governance = new(db);
    private readonly AnalysisProjectSummaryRepository _projectSummary = new(db);
    private readonly AnalysisPortfolioProjectRepository _portfolioProjects = new(db);
    private readonly AnalysisFindingOccurrenceRepository _occurrences = new(db);
    public Task<AnalysisRunContext?> GetRunContextAsync(int runId, CancellationToken ct = default) =>
        _reports.GetRunContextAsync(runId, ct);

    public Task<bool> ArtifactBelongsToRunAsync(int artifactId, int runId, CancellationToken ct = default) =>
        _reports.ArtifactBelongsToRunAsync(artifactId, runId, ct);

    public Task<bool> DastLeaseAuthorizesReportAsync(
        string token,
        int runId,
        string targetHost,
        int targetPort,
        DateTime now,
        CancellationToken ct = default)
        => _reports.DastLeaseAuthorizesReportAsync(token, runId, targetHost, targetPort, now, ct);

    public async Task<AnalysisReportRow?> GetReportByIdentityAsync(
        int runId,
        string scannerKey,
        string? stageName,
        string? stepName,
        string contentHash,
        CancellationToken ct = default)
        => await _reports.GetReportByIdentityAsync(
            runId, scannerKey, stageName, stepName, contentHash, ct).ConfigureAwait(false);

    public Task<AnalysisReportRow?> RecoverReportAfterWriteConflictAsync(
        int runId,
        string scannerKey,
        string? stageName,
        string? stepName,
        string contentHash,
        CancellationToken ct = default)
        => _reports.RecoverReportAfterWriteConflictAsync(
            runId, scannerKey, stageName, stepName, contentHash, ct);

    public async Task<HashSet<string>> GetBaselineFingerprintsAsync(
        int projectId,
        string baselineBranch,
        int currentRunId,
        string scannerKey,
        AnalysisCategory category,
        CancellationToken ct = default)
        => await _reports.GetBaselineFingerprintsAsync(
            projectId, baselineBranch, currentRunId, scannerKey, category, ct).ConfigureAwait(false);

    public async Task<Dictionary<string, AnalysisFinding>> GetFindingsByFingerprintsAsync(
        int projectId,
        IReadOnlyCollection<string> fingerprints,
        CancellationToken ct = default)
        => await _reports.GetFindingsByFingerprintsAsync(projectId, fingerprints, ct).ConfigureAwait(false);

    public async Task AddReportAsync(AnalysisReport report, CancellationToken ct = default)
        => await _reports.AddReportAsync(report, ct).ConfigureAwait(false);

    public Task AddMetricsAsync(int reportId, IReadOnlyCollection<AnalysisMetric> metrics, CancellationToken ct = default)
        => _reports.AddMetricsAsync(reportId, metrics, ct);

    public Task<List<AnalysisPolicy>> GetApplicablePoliciesAsync(
        int organizationId,
        int projectId,
        CancellationToken ct = default)
    {
        return db.AnalysisPolicies.AsNoTracking()
            .Where(policy => (policy.OrganizationId == null || policy.OrganizationId == organizationId)
                && (policy.ProjectId == null || policy.ProjectId == projectId))
            .OrderByDescending(policy => policy.Priority)
            .ThenByDescending(policy => policy.ProjectId == null ? 0 : 1)
            .ThenBy(policy => policy.Id)
            .ToListAsync(ct);
    }

    public Task<List<AnalysisPolicyException>> GetActiveExceptionsAsync(
        int projectId,
        DateTime now,
        CancellationToken ct = default)
    {
        return db.AnalysisPolicyExceptions.AsNoTracking()
            .Where(exception => exception.ProjectId == projectId
                && exception.RevokedAt == null
                && exception.ExpiresAt > now)
            .OrderBy(exception => exception.Id)
            .ToListAsync(ct);
    }

    public async Task AddEvaluationAsync(AnalysisEvaluation evaluation, CancellationToken ct = default)
    {
        db.AnalysisEvaluations.Add(evaluation);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<AnalysisFindingRow> Items, int TotalCount)> GetFindingsAsync(
        int projectId,
        AnalysisFindingPaginationRequest request,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var scanner = string.IsNullOrWhiteSpace(request.Scanner)
            ? null
            : request.Scanner.Trim().ToLowerInvariant();
        var query = ApplyDirectFindingFilters(
            db.AnalysisFindings.AsNoTracking().Where(finding => finding.ProjectId == projectId), request);
        query = await ApplyNewFindingFilterAsync(
            query, projectId, request, scanner, ct).ConfigureAwait(false);
        if (scanner is not null)
            query = query.Where(finding => finding.Occurrences.Any(item =>
                item.ScannerKey.ToLower() == scanner || item.ToolName.ToLower() == scanner));
        if (!string.IsNullOrWhiteSpace(request.Branch))
            query = query.Where(finding => finding.Occurrences.Any(item => item.BranchName == request.Branch));
        query = await ApplyResponsibleFindingFilterAsync(query, projectId, request.Responsible, ct)
            .ConfigureAwait(false);
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var findings = await OrderFindings(query, request).Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        var rows = await BuildFindingRowsAsync(projectId, findings, ct).ConfigureAwait(false);
        return (rows, totalCount);
    }

    private static IQueryable<AnalysisFinding> ApplyDirectFindingFilters(
        IQueryable<AnalysisFinding> query,
        AnalysisFindingPaginationRequest request)
    {
        if (request.Category.HasValue) query = query.Where(finding => finding.Category == request.Category.Value);
        if (request.Severity.HasValue) query = query.Where(finding => finding.Severity == request.Severity.Value);
        if (request.Status.HasValue) query = query.Where(finding => finding.Status == request.Status.Value);
        if (request.Categories is { Count: > 0 } categories) query = query.Where(finding => categories.Contains(finding.Category));
        if (request.Severities is { Count: > 0 } severities) query = query.Where(finding => severities.Contains(finding.Severity));
        if (request.Statuses is { Count: > 0 } statuses) query = query.Where(finding => statuses.Contains(finding.Status));
        // The ID column's number filter.
        if (request.IdFrom is { } idFrom) query = query.Where(finding => finding.Id >= idFrom);
        if (request.IdTo is { } idTo) query = query.Where(finding => finding.Id <= idTo);
        if (request.IdNot is { } idNot) query = query.Where(finding => finding.Id != idNot);
        if (string.IsNullOrWhiteSpace(request.Search)) return query;
        var search = request.Search.Trim().ToLowerInvariant();
        return query.Where(finding => finding.Title.ToLower().Contains(search)
            || finding.Message.ToLower().Contains(search)
            || finding.RuleId.ToLower().Contains(search)
            || (finding.Cwe ?? string.Empty).ToLower().Contains(search));
    }

    private async Task<IQueryable<AnalysisFinding>> ApplyNewFindingFilterAsync(
        IQueryable<AnalysisFinding> query,
        int projectId,
        AnalysisFindingPaginationRequest request,
        string? scanner,
        CancellationToken ct)
    {
        if (!request.IsNew.HasValue) return query;
        var branch = string.IsNullOrWhiteSpace(request.Branch)
            ? await db.Projects.AsNoTracking()
                .Where(project => project.Id == projectId)
                .Select(project => project.DefaultBranch)
                .SingleAsync(ct).ConfigureAwait(false)
            : request.Branch;
        var completedReports = db.AnalysisReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId
                && report.BranchName == branch
                && (report.Status == AnalysisReportStatus.Passed
                    || report.Status == AnalysisReportStatus.Failed));
        if (scanner is not null)
            completedReports = completedReports.Where(report =>
                report.ScannerKey.ToLower() == scanner || report.ScannerName.ToLower() == scanner);
        var latestReportIds = completedReports
            .GroupBy(report => new { report.ScannerKey, report.Category })
            .Select(group => group.OrderByDescending(report => report.CompletedAt)
                .ThenByDescending(report => report.Id).Select(report => report.Id).First());
        return query.Where(finding => finding.Occurrences.Any(occurrence =>
            occurrence.BranchName == branch
            && occurrence.IsNew == request.IsNew.Value
            && (scanner == null
                || occurrence.ScannerKey.ToLower() == scanner
                || occurrence.ToolName.ToLower() == scanner)
            && latestReportIds.Contains(occurrence.AnalysisReportId)));
    }

    private async Task<IQueryable<AnalysisFinding>> ApplyResponsibleFindingFilterAsync(
        IQueryable<AnalysisFinding> query,
        int projectId,
        string? requestedResponsible,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestedResponsible)) return query;
        var responsible = requestedResponsible.Trim().ToLowerInvariant();
        var externalIds = await db.WorkItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId
                && item.ExternalId != null
                && item.ExternalId.StartsWith("analysis:")
                && item.Assignee != null
                && item.Assignee.Username.ToLower().Contains(responsible))
            .Select(item => item.ExternalId!)
            .ToListAsync(ct).ConfigureAwait(false);
        var findingIds = externalIds
            .Select(value => int.TryParse(value.AsSpan("analysis:".Length), out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();
        return query.Where(finding => findingIds.Contains(finding.Id));
    }

    private static IQueryable<AnalysisFinding> OrderFindings(
        IQueryable<AnalysisFinding> query,
        AnalysisFindingPaginationRequest request) =>
        (request.SortBy, request.SortDescending) switch
        {
            ("Id", false) => query.OrderBy(finding => finding.Id),
            ("Id", true) => query.OrderByDescending(finding => finding.Id),
            ("Severity", false) => query.OrderBy(finding => finding.Severity).ThenBy(finding => finding.Id),
            ("Severity", true) => query.OrderByDescending(finding => finding.Severity).ThenByDescending(finding => finding.Id),
            ("RuleId", false) => query.OrderBy(finding => finding.RuleId).ThenBy(finding => finding.Id),
            ("RuleId", true) => query.OrderByDescending(finding => finding.RuleId).ThenByDescending(finding => finding.Id),
            ("FirstSeenAt", false) => query.OrderBy(finding => finding.FirstSeenAt).ThenBy(finding => finding.Id),
            ("FirstSeenAt", true) => query.OrderByDescending(finding => finding.FirstSeenAt).ThenByDescending(finding => finding.Id),
            ("LastSeenAt", false) => query.OrderBy(finding => finding.LastSeenAt).ThenBy(finding => finding.Id),
            _ => query.OrderByDescending(finding => finding.LastSeenAt).ThenByDescending(finding => finding.Id)
        };

    private async Task<List<AnalysisFindingRow>> BuildFindingRowsAsync(
        int projectId,
        IReadOnlyCollection<AnalysisFinding> findings,
        CancellationToken ct)
    {
        var findingIds = findings.Select(finding => finding.Id).ToList();
        var latestOccurrences = findingIds.Count == 0
            ? []
            : await _occurrences.GetLatestAsync(findingIds, ct).ConfigureAwait(false);
        var byFindingId = latestOccurrences.ToDictionary(occurrence => occurrence.AnalysisFindingId);
        var responsibleByFindingId = new Dictionary<int, string>();
        if (findingIds.Count > 0)
        {
            var externalIds = findingIds.Select(id => $"analysis:{id}").ToList();
            var assignments = await db.WorkItems.AsNoTracking()
                .Where(item => item.ProjectId == projectId
                    && item.ExternalId != null
                    && externalIds.Contains(item.ExternalId)
                    && item.Assignee != null)
                .OrderByDescending(item => item.UpdatedAt)
                .Select(item => new { item.ExternalId, item.Assignee!.Username })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var assignment in assignments)
            {
                if (int.TryParse(assignment.ExternalId!.AsSpan("analysis:".Length), out var findingId))
                    responsibleByFindingId.TryAdd(findingId, assignment.Username);
            }
        }
        return findings.Select(finding => new AnalysisFindingRow(
            finding,
            byFindingId.GetValueOrDefault(finding.Id),
            responsibleByFindingId.GetValueOrDefault(finding.Id))).ToList();
    }

    public async Task<AnalysisFindingRow?> GetFindingAsync(int findingId, CancellationToken ct = default)
    {
        var finding = await db.AnalysisFindings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == findingId, ct)
            .ConfigureAwait(false);
        if (finding is null) return null;
        var occurrence = await db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(item => item.AnalysisFindingId == findingId)
            .Include(item => item.AnalysisReport)
                .ThenInclude(report => report.PipelineRun)
                .ThenInclude(run => run!.Pipeline)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var responsible = await db.WorkItems.AsNoTracking()
            .Where(item => item.ProjectId == finding.ProjectId
                && item.ExternalId == $"analysis:{finding.Id}"
                && item.Assignee != null)
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => item.Assignee!.Username)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return new AnalysisFindingRow(finding, occurrence, responsible);
    }

    public Task<List<AnalysisFindingOccurrence>> GetFindingOccurrencesAsync(
        int findingId,
        int take,
        CancellationToken ct = default) =>
        db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(item => item.AnalysisFindingId == findingId)
            .Include(item => item.AnalysisReport)
                .ThenInclude(report => report.PipelineRun)
                .ThenInclude(run => run!.Pipeline)
            .OrderByDescending(item => item.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

    public async Task<int?> GetFindingProjectIdAsync(int findingId, CancellationToken ct = default)
    {
        return await db.AnalysisFindings.AsNoTracking()
            .Where(finding => finding.Id == findingId)
            .Select(finding => (int?)finding.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<AnalysisReportRow> Items, int TotalCount)> GetReportsAsync(
        int projectId,
        PaginationRequest request,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var query = db.AnalysisReports.AsNoTracking().Where(report => report.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim().ToLowerInvariant();
            query = query.Where(report => report.ScannerName.ToLower().Contains(search)
                || report.ScannerKey.ToLower().Contains(search)
                || (report.CommitHash ?? string.Empty).ToLower().Contains(search));
        }
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        query = (request.SortBy, request.SortDescending) switch
        {
            ("ScannerName", false) => query.OrderBy(report => report.ScannerName),
            ("ScannerName", true) => query.OrderByDescending(report => report.ScannerName),
            ("Status", false) => query.OrderBy(report => report.Status),
            ("Status", true) => query.OrderByDescending(report => report.Status),
            ("CompletedAt", false) => query.OrderBy(report => report.CompletedAt),
            _ => query.OrderByDescending(report => report.CompletedAt)
        };

        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .SelectRows()
            .ToListAsync(ct).ConfigureAwait(false);
        return (rows, totalCount);
    }

    public async Task<(List<AnalysisPortfolioRow> Items, int TotalCount)> GetPortfolioAsync(
        IReadOnlyCollection<int>? accessibleProjectIds,
        AnalysisPortfolioPaginationRequest request,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var query = AnalysisPortfolioQuery.ApplyPortfolioFilters(db.AnalysisReports.AsNoTracking(), accessibleProjectIds, request);
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        query = AnalysisPortfolioQuery.OrderPortfolio(query, request);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(report => new AnalysisPortfolioRow(
                report.Id,
                report.OrganizationId,
                report.Organization.Name,
                report.ProjectId,
                report.Project.Name,
                report.PipelineRun == null ? null : report.PipelineRun.PipelineId,
                report.PipelineRun == null ? null : report.PipelineRun.Pipeline.Name,
                report.PipelineRunId,
                report.BranchName,
                report.CommitHash,
                report.Category,
                report.ScannerName,
                report.ScannerVersion,
                report.Status,
                report.Evaluation == null ? null : report.Evaluation.Status,
                report.Evaluation == null ? null : report.Evaluation.Grade,
                report.Evaluation == null
                    ? AnalysisGradeCompleteness.Incomplete
                    : report.Evaluation.GradeCompleteness,
                report.Occurrences.Select(occurrence => occurrence.AnalysisFindingId).Distinct().Count(),
                report.Occurrences.Count(occurrence => occurrence.IsNew),
                report.Evaluation == null ? 0 : report.Evaluation.BlockerCount,
                report.Evaluation == null ? 0 : report.Evaluation.WarningCount,
                report.CompletedAt,
                report.PipelineRun == null ? null : report.PipelineRun.RepositoryUrl,
                null))
            .ToListAsync(ct).ConfigureAwait(false);
        var repositoryIds = await PipelineRunRepositoryLinks
            .ResolveAsync(db, items.Select(item => item.RepositoryUrl), ct).ConfigureAwait(false);
        items = items.Select(item => item with { RepositoryId = PipelineRunRepositoryLinks.Find(repositoryIds, item.RepositoryUrl) })
            .ToList();
        return (items, totalCount);
    }

    public Task<List<AnalysisPortfolioProjectDto>> GetPortfolioProjectsAsync(
        IReadOnlyCollection<int>? accessibleProjectIds,
        CancellationToken ct = default) =>
        _portfolioProjects.GetAsync(accessibleProjectIds, ct);

    /// <summary>Recette R-224: the distinct organizations, projects and scanners of the reports in scope.</summary>
    public async Task<AnalysisPortfolioFilterValuesDto> GetPortfolioFilterValuesAsync(
        IReadOnlyCollection<int>? accessibleProjectIds,
        CancellationToken ct = default)
    {
        var query = AnalysisPortfolioQuery.ApplyPortfolioFilters(db.AnalysisReports.AsNoTracking(), accessibleProjectIds, new AnalysisPortfolioPaginationRequest());
        var organizations = await query.Select(report => report.Organization.Name).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var projects = await query.Select(report => report.Project.Name).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var scanners = await query.Select(report => report.ScannerName).Distinct().ToListAsync(ct).ConfigureAwait(false);
        static List<string> Sorted(IEnumerable<string> values) => [.. values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
        return new AnalysisPortfolioFilterValuesDto
        {
            Organizations = Sorted(organizations),
            Projects = Sorted(projects),
            Scanners = Sorted(scanners)
        };
    }

    public Task<AnalysisProjectSummaryDto> GetProjectSummaryAsync(
        int projectId,
        CancellationToken ct = default) => _projectSummary.GetAsync(projectId, ct);

    public Task<int?> GetProjectOrganizationIdAsync(int projectId, CancellationToken ct = default) =>
        db.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => (int?)project.OrganizationId)
            .FirstOrDefaultAsync(ct);

    public Task<bool> OrganizationExistsAsync(int organizationId, CancellationToken ct = default) =>
        db.Organizations.AsNoTracking().AnyAsync(organization => organization.Id == organizationId, ct);

    public Task<bool> EnvironmentBelongsToProjectAsync(
        string environmentName,
        int projectId,
        CancellationToken ct = default) =>
        db.Environments.AsNoTracking().AnyAsync(
            environment => environment.ProjectId == projectId && environment.Name == environmentName, ct);

    public Task<List<AnalysisExpirationNotice>> GetExpiringGovernanceItemsAsync(
        DateTime now,
        DateTime before,
        int limit,
        CancellationToken ct = default) =>
        _governance.GetExpiringItemsAsync(now, before, limit, ct);

    public Task MarkExpirationNotificationSentAsync(
        string kind,
        int id,
        DateTime sentAt,
        CancellationToken ct = default) =>
        _governance.MarkExpirationNotificationSentAsync(kind, id, sentAt, ct);

    public Task<List<AnalysisPolicy>> GetPoliciesAsync(int projectId, CancellationToken ct = default) =>
        _governance.GetPoliciesAsync(projectId, ct);

    public Task<List<AnalysisPolicy>> GetPoliciesAsyncForConfigurationAsync(
        int? organizationId,
        CancellationToken ct = default) =>
        _governance.GetPoliciesAsyncForConfigurationAsync(organizationId, ct);

    public Task<List<AnalysisPolicy>> GetPoliciesForScopeAsync(
        int? organizationId,
        int? projectId,
        CancellationToken ct = default) =>
        _governance.GetPoliciesForScopeAsync(organizationId, projectId, ct);

    public Task<AnalysisPolicy?> GetPolicyAsync(int policyId, CancellationToken ct = default) =>
        _governance.GetPolicyAsync(policyId, ct);

    public async Task SavePolicyAsync(AnalysisPolicy policy, CancellationToken ct = default)
        => await _governance.SavePolicyAsync(policy, ct).ConfigureAwait(false);

    public async Task SavePolicyRevisionAsync(
        AnalysisPolicy policy,
        DateTime createdAt,
        CancellationToken ct = default)
        => await _governance.SavePolicyRevisionAsync(policy, createdAt, ct).ConfigureAwait(false);

    public Task<List<AnalysisPolicyRevision>> GetPolicyRevisionsAsync(int policyId, CancellationToken ct = default) => _governance.GetPolicyRevisionsAsync(policyId, ct);

    public Task<AnalysisPolicyRevision?> GetPolicyRevisionAsync(int policyId, int version, CancellationToken ct = default) => _governance.GetPolicyRevisionAsync(policyId, version, ct);

    public Task<List<AnalysisPolicyException>> GetExceptionsAsync(int projectId, CancellationToken ct = default) =>
        _governance.GetExceptionsAsync(projectId, ct);

    public Task<AnalysisPolicyException?> GetExceptionAsync(int exceptionId, CancellationToken ct = default) =>
        _governance.GetExceptionAsync(exceptionId, ct);

    public async Task SaveExceptionAsync(AnalysisPolicyException exception, CancellationToken ct = default)
        => await _governance.SaveExceptionAsync(exception, ct).ConfigureAwait(false);

    public Task<AnalysisFinding?> GetTrackedFindingAsync(int findingId, CancellationToken ct = default) =>
        _governance.GetTrackedFindingAsync(findingId, ct);

    public Task<List<AnalysisFindingDecision>> GetFindingDecisionsAsync(int findingId, CancellationToken ct = default) =>
        _governance.GetFindingDecisionsAsync(findingId, ct);

    public async Task SaveFindingDecisionAsync(
        AnalysisFinding finding,
        AnalysisFindingDecision decision,
        CancellationToken ct = default)
        => await _governance.SaveFindingDecisionAsync(finding, decision, ct).ConfigureAwait(false);

    public async Task SaveTrackedChangesAsync(CancellationToken ct = default)
        => await db.SaveChangesAsync(ct).ConfigureAwait(false);

    public async Task ReopenExpiredFindingDecisionsAsync(int projectId, DateTime now, CancellationToken ct = default)
        => await _governance.ReopenExpiredFindingDecisionsAsync(projectId, now, ct).ConfigureAwait(false);

    public Task<AnalysisTrackingProject?> GetTrackingProjectAsync(int projectId, CancellationToken ct = default) =>
        _insights.GetTrackingProjectAsync(projectId, ct);

    public Task<List<AnalysisTrackingProject>> GetActiveTrackingProjectsAsync(
        int limit,
        CancellationToken ct = default) =>
        _insights.GetActiveTrackingProjectsAsync(limit, ct);

    public async Task SaveTrackingSnapshotAsync(
        AnalysisTrackingProject trackingProject,
        IReadOnlyCollection<AnalysisVulnerabilityObservation> observations,
        CancellationToken ct = default)
        => await _insights.SaveTrackingSnapshotAsync(trackingProject, observations, ct).ConfigureAwait(false);

    public async Task<(List<AnalysisVulnerabilityObservation> Items, int TotalCount)> GetVulnerabilityObservationsAsync(
        int projectId,
        PaginationRequest request,
        CancellationToken ct = default)
        => await _insights.GetVulnerabilityObservationsAsync(projectId, request, ct).ConfigureAwait(false);

    public async Task<(int Count, long Bytes)> GetProjectIngestUsageAsync(
        int projectId,
        DateTime since,
        CancellationToken ct = default)
        => await _insights.GetProjectIngestUsageAsync(projectId, since, ct).ConfigureAwait(false);

    public async Task<AnalysisOperationalSnapshot> GetOperationalSnapshotAsync(
        DateTime since,
        CancellationToken ct = default)
        => await _insights.GetOperationalSnapshotAsync(since, ct).ConfigureAwait(false);

    public Task<DependencyTrackGateState> GetDependencyTrackGateStateAsync(
        int runId,
        CancellationToken ct = default) =>
        _insights.GetDependencyTrackGateStateAsync(runId, ct);

    public async Task MarkMissingFindingsFixedAsync(
        int projectId,
        string scannerKey,
        AnalysisCategory category,
        string? branchName,
        IReadOnlyCollection<string> observedFingerprints,
        DateTime now,
        CancellationToken ct = default)
        => await _insights.MarkMissingFindingsFixedAsync(
            projectId, scannerKey, category, branchName, observedFingerprints, now, ct).ConfigureAwait(false);

    public async Task<Dictionary<string, double>> GetBaselineMetricValuesAsync(
        int projectId,
        string baselineBranch,
        int currentRunId,
        IReadOnlyCollection<string> keys,
        CancellationToken ct = default)
        => await _insights.GetBaselineMetricValuesAsync(
            projectId, baselineBranch, currentRunId, keys, ct).ConfigureAwait(false);

    public async Task<(List<AnalysisMetric> Items, int TotalCount)> GetMetricsAsync(
        int projectId, AnalysisMetricPaginationRequest request, CancellationToken ct = default) =>
        await _insights.GetMetricsAsync(projectId, request, ct).ConfigureAwait(false);
    public async Task<(List<AnalysisComponent> Items, int TotalCount)> GetComponentsAsync(int projectId,
        AnalysisComponentPaginationRequest request, CancellationToken ct = default) =>
        await _insights.GetComponentsAsync(projectId, request, ct).ConfigureAwait(false);
    public Task<AnalysisRunGateDto> GetRunGateAsync(int runId, string? scope = null, CancellationToken ct = default) =>
        _insights.GetRunGateAsync(runId, scope, ct);
    public Task<AnalysisRunGateDto> GetRunGateAsync(int runId, CancellationToken ct = default) => GetRunGateAsync(runId, null, ct);
    public Task<AnalysisRunGateDto> GetRunResultSummaryAsync(int runId, int findingLimit, CancellationToken ct = default) =>
        _insights.GetRunGateAsync(runId, null, ct, findingLimit);
    public Task<int?> GetLatestProjectRunIdAsync(int projectId, CancellationToken ct = default) => _insights.GetLatestProjectRunIdAsync(projectId, ct);
}
