// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

internal sealed class AnalysisInsightsRepository(AppDbContext db)
{
    public Task<AnalysisTrackingProject?> GetTrackingProjectAsync(int projectId, CancellationToken ct) =>
        db.AnalysisTrackingProjects.FirstOrDefaultAsync(item => item.ProjectId == projectId, ct);

    public Task<List<AnalysisTrackingProject>> GetActiveTrackingProjectsAsync(int limit, CancellationToken ct) =>
        db.AnalysisTrackingProjects
            .Where(item => item.Active)
            .OrderBy(item => item.LastSyncAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(ct);

    public async Task SaveTrackingSnapshotAsync(
        AnalysisTrackingProject trackingProject,
        IReadOnlyCollection<AnalysisVulnerabilityObservation> observations,
        CancellationToken ct)
    {
        if (trackingProject.Id == 0) db.AnalysisTrackingProjects.Add(trackingProject);
        if (observations.Count > 0) db.AnalysisVulnerabilityObservations.AddRange(observations);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<AnalysisVulnerabilityObservation> Items, int TotalCount)> GetVulnerabilityObservationsAsync(
        int projectId,
        PaginationRequest request,
        CancellationToken ct)
    {
        var (page, pageSize) = request.Normalize();
        var query = db.AnalysisVulnerabilityObservations.AsNoTracking().Where(item => item.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim().ToLowerInvariant();
            query = query.Where(item => item.VulnerabilityId.ToLower().Contains(search)
                || item.ComponentName.ToLower().Contains(search)
                || (item.PackageUrl ?? string.Empty).ToLower().Contains(search));
        }
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (request.SortBy, request.SortDescending) switch
        {
            ("Severity", false) => query.OrderBy(item => item.Severity),
            ("Severity", true) => query.OrderByDescending(item => item.Severity),
            ("VulnerabilityId", false) => query.OrderBy(item => item.VulnerabilityId),
            ("VulnerabilityId", true) => query.OrderByDescending(item => item.VulnerabilityId),
            ("ObservedAt", false) => query.OrderBy(item => item.ObservedAt),
            _ => query.OrderByDescending(item => item.ObservedAt)
        };
        return (await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false), total);
    }

    public async Task<(int Count, long Bytes)> GetProjectIngestUsageAsync(
        int projectId,
        DateTime since,
        CancellationToken ct)
    {
        var query = db.AnalysisReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId && report.CreatedAt >= since);
        return (await query.CountAsync(ct).ConfigureAwait(false),
            await query.SumAsync(report => (long?)report.ContentSize, ct).ConfigureAwait(false) ?? 0);
    }

    public async Task<AnalysisOperationalSnapshot> GetOperationalSnapshotAsync(DateTime since, CancellationToken ct)
    {
        var tracking = await db.AnalysisTrackingProjects.AsNoTracking()
            .Where(item => item.Active)
            .Select(item => new AnalysisTrackingHealthRow(
                item.OrganizationId, item.ProjectId, item.SyncStatus, item.LastSyncAt, item.LastError))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        // Only servers whose report is still current. The capabilities column holds whatever the agent
        // last said, and it is never cleared: without this, a runner that has been offline for weeks -
        // or retired from the fleet - keeps raising the same operational alert forever, on a workspace
        // that may no longer exist. An alert about a machine nobody is running is noise, and the
        // operator has no way to make it stop.
        var servers = await db.Servers.AsNoTracking()
            .Where(server => server.ScannerCapabilitiesJson != null && server.LastHeartbeat >= since)
            .Select(server => new AnalysisServerHealthRow(
                server.OrganizationId, server.Id, server.Name, server.ScannerCapabilitiesJson))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var storage = await db.AnalysisReports.AsNoTracking()
            .Where(report => report.CreatedAt >= since)
            .GroupBy(report => new { report.OrganizationId, report.ProjectId })
            .Select(group => new AnalysisStorageHealthRow(
                group.Key.OrganizationId, group.Key.ProjectId, group.Count(), group.Sum(report => report.ContentSize)))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new AnalysisOperationalSnapshot(tracking, servers, storage);
    }

    public async Task MarkMissingFindingsFixedAsync(
        int projectId,
        string scannerKey,
        AnalysisCategory category,
        string? branchName,
        IReadOnlyCollection<string> observedFingerprints,
        DateTime now,
        CancellationToken ct)
    {
        var candidates = await db.AnalysisFindings
            .Where(finding => finding.ProjectId == projectId
                && finding.Category == category
                // Recette R2-135: a finding the scanner no longer sees is fixed whatever was decided on
                // it (accepted, false positive, mitigated); its decisions stay in its history.
                && finding.Status != AnalysisFindingStatus.Fixed
                && !observedFingerprints.Contains(finding.Fingerprint)
                && finding.Occurrences.Any(occurrence => occurrence.ScannerKey == scannerKey
                    && occurrence.BranchName == branchName))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (candidates.Count == 0) return;

        var candidateIds = candidates.Select(item => item.Id).ToList();
        var historicalScanners = await db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(occurrence => candidateIds.Contains(occurrence.AnalysisFindingId)
                && occurrence.BranchName == branchName
                && occurrence.AnalysisFinding.Category == category)
            .Select(occurrence => occurrence.ScannerKey)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var otherScanners = historicalScanners
            .Where(item => !string.Equals(item, scannerKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var latestReportIds = await db.AnalysisReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId
                && report.Category == category
                && report.BranchName == branchName
                && otherScanners.Contains(report.ScannerKey))
            .GroupBy(report => report.ScannerKey)
            .Select(group => group
                .OrderByDescending(report => report.CompletedAt)
                .ThenByDescending(report => report.Id)
                .Select(report => report.Id)
                .First())
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var stillObservedByAnotherScanner = (await db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(occurrence => latestReportIds.Contains(occurrence.AnalysisReportId)
                && candidateIds.Contains(occurrence.AnalysisFindingId))
            .Select(occurrence => occurrence.AnalysisFindingId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false)).ToHashSet();
        foreach (var finding in candidates)
        {
            if (stillObservedByAnotherScanner.Contains(finding.Id)) continue;
            finding.Status = AnalysisFindingStatus.Fixed;
            finding.ResolvedAt = now;
            finding.UpdatedAt = now;
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, double>> GetBaselineMetricValuesAsync(
        int projectId,
        string baselineBranch,
        int currentRunId,
        IReadOnlyCollection<string> keys,
        CancellationToken ct)
    {
        if (keys.Count == 0) return [];
        var baselineRunId = await db.AnalysisReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId
                && report.PipelineRunId != null
                && report.PipelineRunId != currentRunId
                && report.BranchName == baselineBranch
                && report.PipelineRun!.Status == PipelineStatus.Success
                && report.Metrics.Any(metric => keys.Contains(metric.Key)))
            .OrderByDescending(report => report.CompletedAt)
            .Select(report => report.PipelineRunId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (!baselineRunId.HasValue) return [];
        var metrics = await db.AnalysisMetrics.AsNoTracking()
            .Where(metric => metric.AnalysisReport.PipelineRunId == baselineRunId && keys.Contains(metric.Key))
            .OrderByDescending(metric => metric.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return metrics.GroupBy(metric => AnalysisMetricIdentity.Build(
                metric.Key, metric.Scope, metric.Language, metric.FilePath, metric.Symbol),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<(List<AnalysisMetric> Items, int TotalCount)> GetMetricsAsync(
        int projectId,
        AnalysisMetricPaginationRequest request,
        CancellationToken ct)
    {
        var (page, pageSize) = request.Normalize();
        var query = db.AnalysisMetrics.AsNoTracking().Where(metric => metric.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(request.Key)) query = query.Where(metric => metric.Key == request.Key);
        if (!string.IsNullOrWhiteSpace(request.Language)) query = query.Where(metric => metric.Language == request.Language);
        if (!string.IsNullOrWhiteSpace(request.Branch)) query = query.Where(metric => metric.AnalysisReport.BranchName == request.Branch);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim().ToLowerInvariant();
            query = query.Where(metric => metric.Key.ToLower().Contains(search)
                || metric.ToolName.ToLower().Contains(search)
                || (metric.FilePath ?? string.Empty).ToLower().Contains(search));
        }
        if (request.LatestReportOnly)
        {
            var latestReportId = await query
                .OrderByDescending(metric => metric.AnalysisReport.CompletedAt)
                .ThenByDescending(metric => metric.AnalysisReportId)
                .Select(metric => (int?)metric.AnalysisReportId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (!latestReportId.HasValue) return ([], 0);
            query = query.Where(metric => metric.AnalysisReportId == latestReportId.Value);
        }
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (request.SortBy, request.SortDescending) switch
        {
            ("Key", false) => query.OrderBy(metric => metric.Key),
            ("Key", true) => query.OrderByDescending(metric => metric.Key),
            ("Value", false) => query.OrderBy(metric => metric.Value),
            ("Value", true) => query.OrderByDescending(metric => metric.Value),
            ("CreatedAt", false) => query.OrderBy(metric => metric.CreatedAt),
            _ => query.OrderByDescending(metric => metric.CreatedAt)
        };
        return (await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false), total);
    }

    public async Task<(List<AnalysisComponent> Items, int TotalCount)> GetComponentsAsync(
        int projectId,
        AnalysisComponentPaginationRequest request,
        CancellationToken ct)
    {
        var (page, pageSize) = request.Normalize();
        var query = db.AnalysisComponents.AsNoTracking().Where(component => component.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(request.ComponentType)) query = query.Where(component => component.ComponentType == request.ComponentType);
        if (!string.IsNullOrWhiteSpace(request.Branch)) query = query.Where(component => component.AnalysisReport.BranchName == request.Branch);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim().ToLowerInvariant();
            query = query.Where(component => component.Name.ToLower().Contains(search)
                || component.Version.ToLower().Contains(search)
                || (component.PackageUrl ?? string.Empty).ToLower().Contains(search));
        }
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (request.SortBy, request.SortDescending) switch
        {
            ("Name", false) => query.OrderBy(component => component.Name),
            ("Name", true) => query.OrderByDescending(component => component.Name),
            ("Version", false) => query.OrderBy(component => component.Version),
            ("Version", true) => query.OrderByDescending(component => component.Version),
            _ => query.OrderBy(component => component.Name).ThenBy(component => component.Version)
        };
        return (await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false), total);
    }

    /// <param name="findingLimit">Recette R-485: when set, only the most severe findings are listed and
    /// the counts are worked out by the database over all of them (the run page reads the rest page by
    /// page); unset, every finding is listed, as the agent's gate report needs.</param>
    public async Task<AnalysisRunGateDto> GetRunGateAsync(
        int runId,
        string? scope,
        CancellationToken ct,
        int? findingLimit = null)
    {
        var normalizedScope = AnalysisGateScopes.Normalize(scope);
        var categoryFilter = AnalysisGateScopes.IsValid(normalizedScope)
            ? AnalysisGateScopes.Categories(normalizedScope).ToList()
            : null;
        var expectedProducers = await db.Tasks.AsNoTracking()
            .Where(task => task.PipelineRunId == runId
                && (task.Operation == OperationKind.PipelineRunScanner
                    || task.Operation == OperationKind.PipelinePublishCoverage
                    || task.Operation == OperationKind.PipelinePublishLint
                    || task.Operation == OperationKind.PipelinePublishComplexity
                    || task.Operation == OperationKind.PipelinePublishMutation))
            .OrderBy(task => task.Id)
            .Select(task => new ExpectedAnalysisProducer(task.Id, task.Operation, task.Command, task.Name))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (categoryFilter is not null)
            expectedProducers = expectedProducers
                .Where(producer => IsProducerForScope(producer, normalizedScope))
                .ToList();
        var reportQuery = db.AnalysisReports.AsNoTracking()
            .Where(report => report.PipelineRunId == runId);
        if (categoryFilter is not null)
            reportQuery = reportQuery.Where(report => categoryFilter.Contains(report.Category));
        var reports = await reportQuery
            .OrderBy(report => report.Id)
            .Select(report => new AnalysisRunGateReportDto
            {
                ReportId = report.Id,
                PipelineArtifactId = report.PipelineArtifactId,
                ScannerKey = report.ScannerKey,
                ScannerName = report.ScannerName,
                ScannerVersion = report.ScannerVersion,
                StageName = report.StageName,
                StepName = report.StepName,
                Category = report.Category,
                ReportStatus = report.Status,
                GateStatus = report.Evaluation == null ? null : report.Evaluation.Status,
                Grade = report.Evaluation == null ? null : report.Evaluation.Grade,
                GradeCompleteness = report.Evaluation == null
                     ? AnalysisGradeCompleteness.Incomplete
                     : report.Evaluation.GradeCompleteness,
                FindingCount = report.Occurrences.Select(occurrence => occurrence.AnalysisFindingId).Distinct().Count(),
                NewFindingCount = report.Occurrences.Count(occurrence => occurrence.IsNew),
                ComponentCount = report.Components.Count,
                MetricCount = report.Metrics.Count,
                BlockerCount = report.Evaluation == null ? 0 : report.Evaluation.BlockerCount,
                WarningCount = report.Evaluation == null ? 0 : report.Evaluation.WarningCount
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var findingQuery = db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(occurrence => occurrence.AnalysisReport.PipelineRunId == runId);
        if (categoryFilter is not null)
            findingQuery = findingQuery.Where(occurrence =>
                categoryFilter.Contains(occurrence.AnalysisReport.Category));
        var orderedFindings = AnalysisRunResultRepository.GroupFindings(findingQuery)
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.FindingId);
        var findings = await (findingLimit is { } limit ? orderedFindings.Take(limit) : orderedFindings)
            .ToListAsync(ct).ConfigureAwait(false);
        var findingCounts = findingLimit is null
            ? (Open: findings.Count(finding => finding.Status == AnalysisFindingStatus.Open),
                NewOpen: findings.Count(finding => finding.IsNew && finding.Status == AnalysisFindingStatus.Open),
                Decided: findings.Count(finding => finding.Status != AnalysisFindingStatus.Open))
            : await AnalysisRunResultRepository.CountFindingsAsync(findingQuery, ct).ConfigureAwait(false);
        var evaluationQuery = db.AnalysisEvaluations.AsNoTracking()
            .Where(evaluation => evaluation.PipelineRunId == runId);
        if (categoryFilter is not null)
            evaluationQuery = evaluationQuery.Where(evaluation =>
                categoryFilter.Contains(evaluation.AnalysisReport.Category));
        var evaluationSnapshots = await evaluationQuery
            .OrderBy(evaluation => evaluation.AnalysisReportId)
             .Select(evaluation => new AnalysisEvaluationSnapshot(
                 evaluation.AnalysisReportId,
                 evaluation.PolicySnapshotJson,
                 evaluation.GradeSnapshotJson))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var violations = AnalysisGateViolationParser.Parse(evaluationSnapshots);
        var grade = AnalysisGradeEngine.Aggregate(
            evaluationSnapshots.Select(snapshot => snapshot.GradeSnapshotJson),
            pipelineRunId: runId);

        var missingProducers = FindMissingProducers(expectedProducers, reports);
        var status = ResolveGateStatus(reports, missingProducers, grade);
        return new AnalysisRunGateDto
        {
            PipelineRunId = runId,
            Status = status,
            ReportCount = reports.Count,
            // The gate grid materializes findings, not occurrences. A finding can be reported by
            // several producers in the same run, so report-level totals would overstate the number
            // of rows visible to the user. Occurrences remain available from the finding detail.
            // Recette R-527: a finding someone decided on (accepted, false positive, mitigated) is not
            // something left to do; it is counted apart and the page hides it until asked.
            FindingCount = findingCounts.Open,
            NewFindingCount = findingCounts.NewOpen,
            DecidedFindingCount = findingCounts.Decided,
            ComponentCount = reports.Sum(report => report.ComponentCount),
            MetricCount = reports.Sum(report => report.MetricCount),
            BlockerCount = reports.Sum(report => report.BlockerCount),
            WarningCount = reports.Sum(report => report.WarningCount),
            Grade = grade,
            MissingProducers = missingProducers,
            Reports = reports,
            Findings = findings,
            Violations = violations
        };
    }

    /// <summary>The run's gate verdict: an operational error (no report, a missing or failed producer,
    /// an incomplete grade) wins over a block, a block (a blocking report or a grade below the minimum)
    /// over a warning. Extracted from <see cref="GetRunGateAsync"/> to keep it under the complexity budget.</summary>
    private static AnalysisGateStatus ResolveGateStatus(
        List<AnalysisRunGateReportDto> reports,
        List<string> missingProducers,
        AnalysisGradeSummaryDto? grade)
    {
        var hasOperationalError = reports.Count == 0 || missingProducers.Count > 0 || reports.Any(report =>
            report.ReportStatus is AnalysisReportStatus.Error or AnalysisReportStatus.TimedOut or AnalysisReportStatus.Unavailable
            || report.GateStatus is null or AnalysisGateStatus.Error)
            || grade?.Completeness == AnalysisGradeCompleteness.Incomplete;
        if (hasOperationalError) return AnalysisGateStatus.Error;
        var gradeBlocks = grade?.OverallGrade.HasValue == true
            && grade.MinimumGrade.HasValue
            && grade.OverallGrade.Value > grade.MinimumGrade.Value;
        if (gradeBlocks || reports.Any(report => report.GateStatus == AnalysisGateStatus.Blocked))
            return AnalysisGateStatus.Blocked;
        return reports.Any(report => report.GateStatus == AnalysisGateStatus.Warning)
            ? AnalysisGateStatus.Warning
            : AnalysisGateStatus.Passed;
    }

    private static bool IsProducerForScope(ExpectedAnalysisProducer producer, string scope)
    {
        if (producer.Operation is OperationKind.PipelinePublishCoverage
            or OperationKind.PipelinePublishLint
            or OperationKind.PipelinePublishComplexity
            or OperationKind.PipelinePublishMutation)
            return scope == AnalysisGateScopes.Quality;
        if (producer.Operation != OperationKind.PipelineRunScanner) return false;
        return AnalysisGateScopes.ForScannerCategory(
            ScannerManifestCatalog.Find(producer.Command)?.Category) == scope;
    }

    public Task<int?> GetLatestProjectRunIdAsync(int projectId, CancellationToken ct) =>
        db.PipelineRuns.AsNoTracking()
            .Where(run => run.Pipeline.ProjectId == projectId)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .Select(run => (int?)run.Id)
            .FirstOrDefaultAsync(ct);

    public async Task<DependencyTrackGateState> GetDependencyTrackGateStateAsync(
        int runId,
        CancellationToken ct)
    {
        var hasSbom = await db.AnalysisReports.AsNoTracking()
            .AnyAsync(report => report.PipelineRunId == runId
                && report.Category == AnalysisCategory.Sbom
                && (report.Status == AnalysisReportStatus.Passed
                    || report.Status == AnalysisReportStatus.Failed), ct)
            .ConfigureAwait(false);
        var statuses = await db.DependencyTrackOutboxItems.AsNoTracking()
            .Where(item => item.AnalysisReport.PipelineRunId == runId)
            .Select(item => item.Status)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new DependencyTrackGateState(hasSbom, statuses);
    }

    private static List<string> FindMissingProducers(
        IReadOnlyCollection<ExpectedAnalysisProducer> expectedProducers,
        IReadOnlyCollection<AnalysisRunGateReportDto> reports)
    {
        var missing = new List<string>();
        var remainingReports = reports.ToList();
        foreach (var producer in expectedProducers)
        {
            IReadOnlyList<string> expectedKeys = producer.Operation switch
            {
                OperationKind.PipelineRunScanner => [producer.Command],
                OperationKind.PipelinePublishLint => ["pipeline-sarif-lint"],
                OperationKind.PipelinePublishComplexity =>
                    ["roslyn-metrics", "dotnet-architecture-metrics", "dotnet-architecture"],
                OperationKind.PipelinePublishCoverage => [],
                OperationKind.PipelinePublishMutation => ["stryker-mutation"],
                _ => []
            };
            if (producer.Operation == OperationKind.PipelinePublishCoverage)
            {
                var coverage = remainingReports.FirstOrDefault(report => report.Category == AnalysisCategory.Coverage);
                if (coverage is null) missing.Add($"coverage-task:{producer.Id}");
                else remainingReports.Remove(coverage);
                continue;
            }
            foreach (var key in expectedKeys)
            {
                var requiresExactStep = producer.Operation == OperationKind.PipelineRunScanner
                    && string.Equals(
                        ScannerManifestCatalog.Find(producer.Command)?.Category,
                        nameof(AnalysisCategory.Dast),
                        StringComparison.OrdinalIgnoreCase);
                var report = remainingReports.FirstOrDefault(item =>
                    string.Equals(item.ScannerKey, key, StringComparison.OrdinalIgnoreCase)
                    && (!requiresExactStep
                        || string.Equals(item.StepName, producer.StepName, StringComparison.Ordinal)));
                if (report is null) missing.Add($"{key}:task:{producer.Id}");
                else remainingReports.Remove(report);
            }
        }
        if (expectedProducers.Any(producer =>
                producer.Operation == OperationKind.PipelineRunScanner
                && string.Equals(
                    ScannerManifestCatalog.Find(producer.Command)?.Category,
                    nameof(AnalysisCategory.Dast),
                    StringComparison.OrdinalIgnoreCase)))
        {
            missing.AddRange(remainingReports
                .Where(report => report.Category == AnalysisCategory.Dast)
                .Select(report => $"unexpected-report:{report.ReportId}"));
        }
        return missing;
    }

    private sealed record ExpectedAnalysisProducer(
        int Id,
        OperationKind Operation,
        string Command,
        string StepName);
}

public sealed record DependencyTrackGateState(bool HasSbom, IReadOnlyList<string> Statuses);
