// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

public interface IAnalysisRepository
{
    Task<AnalysisRunContext?> GetRunContextAsync(int runId, CancellationToken ct = default);
    Task<bool> ArtifactBelongsToRunAsync(int artifactId, int runId, CancellationToken ct = default);
    Task<bool> DastLeaseAuthorizesReportAsync(
        string token,
        int runId,
        string targetHost,
        int targetPort,
        DateTime now,
        CancellationToken ct = default);
    Task<AnalysisReportRow?> GetReportByIdentityAsync(
        int runId, string scannerKey, string? stageName, string? stepName,
        string contentHash, CancellationToken ct = default);
    Task<AnalysisReportRow?> RecoverReportAfterWriteConflictAsync(
        int runId, string scannerKey, string? stageName, string? stepName,
        string contentHash, CancellationToken ct = default);
    Task<HashSet<string>> GetBaselineFingerprintsAsync(int projectId, string baselineBranch, int currentRunId,
        string scannerKey, AnalysisCategory category, CancellationToken ct = default);
    Task<Dictionary<string, AnalysisFinding>> GetFindingsByFingerprintsAsync(int projectId, IReadOnlyCollection<string> fingerprints, CancellationToken ct = default);
    Task AddReportAsync(AnalysisReport report, CancellationToken ct = default);
    Task AddMetricsAsync(int reportId, IReadOnlyCollection<AnalysisMetric> metrics, CancellationToken ct = default);
    Task<List<AnalysisPolicy>> GetApplicablePoliciesAsync(int organizationId, int projectId, CancellationToken ct = default);
    Task<List<AnalysisPolicyException>> GetActiveExceptionsAsync(int projectId, DateTime now, CancellationToken ct = default);
    Task AddEvaluationAsync(AnalysisEvaluation evaluation, CancellationToken ct = default);
    Task<(List<AnalysisFindingRow> Items, int TotalCount)> GetFindingsAsync(int projectId, AnalysisFindingPaginationRequest request, CancellationToken ct = default);
    Task<AnalysisFindingRow?> GetFindingAsync(int findingId, CancellationToken ct = default);
    Task<List<AnalysisFindingOccurrence>> GetFindingOccurrencesAsync(int findingId, int take, CancellationToken ct = default);
    Task<int?> GetFindingProjectIdAsync(int findingId, CancellationToken ct = default);
    Task<(List<AnalysisReportRow> Items, int TotalCount)> GetReportsAsync(int projectId, PaginationRequest request, CancellationToken ct = default);
    Task<(List<AnalysisPortfolioRow> Items, int TotalCount)> GetPortfolioAsync(
        IReadOnlyCollection<int>? accessibleProjectIds,
        AnalysisPortfolioPaginationRequest request,
        CancellationToken ct = default);
    Task<List<AnalysisPortfolioProjectDto>> GetPortfolioProjectsAsync(
        IReadOnlyCollection<int>? accessibleProjectIds,
        CancellationToken ct = default);
    Task<AnalysisPortfolioFilterValuesDto> GetPortfolioFilterValuesAsync(
        IReadOnlyCollection<int>? accessibleProjectIds,
        CancellationToken ct = default);
    Task<AnalysisProjectSummaryDto> GetProjectSummaryAsync(int projectId, CancellationToken ct = default);
    Task<int?> GetProjectOrganizationIdAsync(int projectId, CancellationToken ct = default);
    Task<bool> OrganizationExistsAsync(int organizationId, CancellationToken ct = default);
    Task<bool> EnvironmentBelongsToProjectAsync(string environmentName, int projectId, CancellationToken ct = default);
    Task<List<AnalysisPolicy>> GetPoliciesAsync(int projectId, CancellationToken ct = default);
    Task<List<AnalysisPolicy>> GetPoliciesAsyncForConfigurationAsync(
        int? organizationId,
        CancellationToken ct = default);
    Task<List<AnalysisPolicy>> GetPoliciesForScopeAsync(int? organizationId, int? projectId, CancellationToken ct = default);
    Task<AnalysisPolicy?> GetPolicyAsync(int policyId, CancellationToken ct = default);
    Task SavePolicyAsync(AnalysisPolicy policy, CancellationToken ct = default);
    Task SavePolicyRevisionAsync(AnalysisPolicy policy, DateTime createdAt, CancellationToken ct = default);
    Task<List<AnalysisPolicyRevision>> GetPolicyRevisionsAsync(int policyId, CancellationToken ct = default);
    Task<AnalysisPolicyRevision?> GetPolicyRevisionAsync(
        int policyId, int version, CancellationToken ct = default);
    Task<List<AnalysisPolicyException>> GetExceptionsAsync(int projectId, CancellationToken ct = default);
    Task<AnalysisPolicyException?> GetExceptionAsync(int exceptionId, CancellationToken ct = default);
    Task SaveExceptionAsync(AnalysisPolicyException exception, CancellationToken ct = default);
    Task<AnalysisFinding?> GetTrackedFindingAsync(int findingId, CancellationToken ct = default);
    Task<List<AnalysisFindingDecision>> GetFindingDecisionsAsync(int findingId, CancellationToken ct = default);
    Task SaveFindingDecisionAsync(AnalysisFinding finding, AnalysisFindingDecision decision, CancellationToken ct = default);
    /// <summary>Saves the changes made to an entity read with <see cref="GetTrackedFindingAsync"/>.</summary>
    Task SaveTrackedChangesAsync(CancellationToken ct = default);
    Task ReopenExpiredFindingDecisionsAsync(int projectId, DateTime now, CancellationToken ct = default);
    Task MarkMissingFindingsFixedAsync(int projectId, string scannerKey, AnalysisCategory category, string? branchName,
        IReadOnlyCollection<string> observedFingerprints, DateTime now, CancellationToken ct = default);
    Task<Dictionary<string, double>> GetBaselineMetricValuesAsync(int projectId, string baselineBranch,
        int currentRunId, IReadOnlyCollection<string> keys, CancellationToken ct = default);
    Task<(List<AnalysisMetric> Items, int TotalCount)> GetMetricsAsync(int projectId,
        AnalysisMetricPaginationRequest request, CancellationToken ct = default);
    Task<(List<AnalysisComponent> Items, int TotalCount)> GetComponentsAsync(int projectId,
        AnalysisComponentPaginationRequest request, CancellationToken ct = default);
    Task<List<AnalysisExpirationNotice>> GetExpiringGovernanceItemsAsync(
        DateTime now, DateTime before, int limit, CancellationToken ct = default);
    Task MarkExpirationNotificationSentAsync(
        string kind, int id, DateTime sentAt, CancellationToken ct = default);
    Task<AnalysisTrackingProject?> GetTrackingProjectAsync(int projectId, CancellationToken ct = default);
    Task<List<AnalysisTrackingProject>> GetActiveTrackingProjectsAsync(int limit, CancellationToken ct = default);
    Task SaveTrackingSnapshotAsync(AnalysisTrackingProject trackingProject,
        IReadOnlyCollection<AnalysisVulnerabilityObservation> observations, CancellationToken ct = default);
    Task<(List<AnalysisVulnerabilityObservation> Items, int TotalCount)> GetVulnerabilityObservationsAsync(
        int projectId, PaginationRequest request, CancellationToken ct = default);
    Task<(int Count, long Bytes)> GetProjectIngestUsageAsync(int projectId, DateTime since, CancellationToken ct = default);
    Task<AnalysisOperationalSnapshot> GetOperationalSnapshotAsync(DateTime since, CancellationToken ct = default);
    Task<AnalysisRunGateDto> GetRunGateAsync(
        int runId, string? scope = null, CancellationToken ct = default);
    Task<AnalysisRunGateDto> GetRunGateAsync(int runId, CancellationToken ct = default);
    /// <summary>Recette R-485: the run result the run page reads: every count, the most severe
    /// <paramref name="findingLimit"/> findings (the rest are read page by page).</summary>
    Task<AnalysisRunGateDto> GetRunResultSummaryAsync(int runId, int findingLimit, CancellationToken ct = default);
    Task<int?> GetLatestProjectRunIdAsync(int projectId, CancellationToken ct = default);
    Task<DependencyTrackGateState> GetDependencyTrackGateStateAsync(
        int runId,
        CancellationToken ct = default);
}
