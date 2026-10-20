// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Analysis;

public sealed record AnalysisReportDto
{
    public int Id { get; init; }
    public int OrganizationId { get; init; }
    public int ProjectId { get; init; }
    public int? PipelineRunId { get; init; }
    public int? PipelineArtifactId { get; init; }
    public string ScannerKey { get; init; } = string.Empty;
    public string ScannerName { get; init; } = string.Empty;
    public string ScannerVersion { get; init; } = string.Empty;
    public AnalysisCategory Category { get; init; }
    public AnalysisReportStatus Status { get; init; }
    public AnalysisReportFormat Format { get; init; }
    public string? ReportPath { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public long ContentSize { get; init; }
    public string? BranchName { get; init; }
    public string? EnvironmentName { get; init; }
    public string? CommitHash { get; init; }
    public string? StageName { get; init; }
    public string? StepName { get; init; }
    public string? RuleSetHash { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public bool IsTruncated { get; init; }
    public string? ErrorMessage { get; init; }
    public int FindingCount { get; init; }
    public int NewFindingCount { get; init; }
    public int ComponentCount { get; init; }
    public int MetricCount { get; init; }
    public AnalysisGateStatus? GateStatus { get; init; }
    public AnalysisGrade? Grade { get; init; }
    public AnalysisGradeCompleteness GradeCompleteness { get; init; }
    public int BlockerCount { get; init; }
    public int WarningCount { get; init; }
}

public sealed record AnalysisRunGateDto
{
    /// <summary><c>GET runs/{id}/result</c> lists at most this many findings, the most severe; the
    /// counts cover them all and <c>GET runs/findings</c> pages through the rest.</summary>
    public const int SummaryFindingLimit = 50;

    public int PipelineRunId { get; init; }
    public AnalysisGateStatus Status { get; init; }
    public int ReportCount { get; init; }
    public int FindingCount { get; init; }
    public int NewFindingCount { get; init; }
    /// <summary>The findings of the run that are no longer open (accepted, false positive,
    /// mitigated, fixed since). They are left out of <see cref="FindingCount"/> and <see
    /// cref="NewFindingCount"/>; the list carries them, hidden until asked for.</summary>
    public int DecidedFindingCount { get; init; }
    public int ComponentCount { get; init; }
    public int MetricCount { get; init; }
    public int BlockerCount { get; init; }
    public int WarningCount { get; init; }
    public AnalysisGradeSummaryDto? Grade { get; init; }
    public IReadOnlyList<string> MissingProducers { get; init; } = [];
    public IReadOnlyList<AnalysisRunGateReportDto> Reports { get; init; } = [];
    public IReadOnlyList<AnalysisRunGateFindingDto> Findings { get; init; } = [];
    public IReadOnlyList<AnalysisRunGateViolationDto> Violations { get; init; } = [];
}

public sealed record AnalysisRunGateViolationDto
{
    public int ReportId { get; init; }
    public string PolicyKey { get; init; } = string.Empty;
    public AnalysisPolicyScope Scope { get; init; }
    public int Version { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string TargetKind { get; init; } = string.Empty;
    public string? TargetKey { get; init; }
    public string? ObservedValue { get; init; }
    public string? Operator { get; init; }
    public string? Threshold { get; init; }
    public int Count { get; init; }
    public string? PolicySnapshotHash { get; init; }
}

public sealed record AnalysisRunGateReportDto
{
    public int ReportId { get; init; }
    public int? PipelineArtifactId { get; init; }
    public string ScannerKey { get; init; } = string.Empty;
    public string ScannerName { get; init; } = string.Empty;
    public string ScannerVersion { get; init; } = string.Empty;
    public string? StageName { get; init; }
    public string? StepName { get; init; }
    public AnalysisCategory Category { get; init; }
    public AnalysisReportStatus ReportStatus { get; init; }
    public AnalysisGateStatus? GateStatus { get; init; }
    public AnalysisGrade? Grade { get; init; }
    public AnalysisGradeCompleteness GradeCompleteness { get; init; }
    public int FindingCount { get; init; }
    public int NewFindingCount { get; init; }
    public int ComponentCount { get; init; }
    public int MetricCount { get; init; }
    public int BlockerCount { get; init; }
    public int WarningCount { get; init; }
}

public sealed record AnalysisRunGateFindingDto
{
    public int FindingId { get; init; }
    public string RuleId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public AnalysisCategory Category { get; init; }
    public AnalysisSeverity Severity { get; init; }
    public AnalysisFindingStatus Status { get; init; }
    public string? FilePath { get; init; }
    public int? StartLine { get; init; }
    public bool IsNew { get; init; }
}

public sealed record AnalysisFindingDto
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public int FingerprintVersion { get; init; }
    public string RuleId { get; init; } = string.Empty;
    public AnalysisCategory Category { get; init; }
    public AnalysisSeverity Severity { get; init; }
    public AnalysisConfidence Confidence { get; init; }
    public string? Cwe { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? HelpUri { get; init; }
    public AnalysisFindingStatus Status { get; init; }
    public DateTime FirstSeenAt { get; init; }
    public DateTime LastSeenAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? Responsible { get; init; }
    public AnalysisFindingOccurrenceDto? LatestOccurrence { get; init; }
}

public sealed record AnalysisFindingOccurrenceDto
{
    public int Id { get; init; }
    public int AnalysisReportId { get; init; }
    public int? PipelineRunId { get; init; }
    public int? RepositoryId { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string ScannerKey { get; init; } = string.Empty;
    public string RuleId { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public string? Symbol { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? BranchName { get; init; }
    public string? CommitHash { get; init; }
    public bool IsNew { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record AnalysisMetricDto
{
    public int Id { get; init; }
    public int AnalysisReportId { get; init; }
    public string Key { get; init; } = string.Empty;
    public double Value { get; init; }
    public string? Unit { get; init; }
    public string? Scope { get; init; }
    public string? Language { get; init; }
    public string? FilePath { get; init; }
    public string? Symbol { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public double? BaselineValue { get; init; }
    public AnalysisMetricDirection Direction { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record AnalysisComponentDto
{
    public int Id { get; init; }
    public int AnalysisReportId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? PackageUrl { get; init; }
    public string? ComponentType { get; init; }
    public IReadOnlyList<string> Licenses { get; init; } = [];
    public string? Hash { get; init; }
    public bool IsDirect { get; init; }
}

public sealed record PublishAnalysisReportRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string ScannerKey { get; init; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string ScannerName { get; init; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string ScannerVersion { get; init; } = string.Empty;

    public AnalysisCategory Category { get; init; }
    public AnalysisReportStatus Status { get; init; }
    public AnalysisReportFormat Format { get; init; }

    [StringLength(100 * 1024 * 1024)]
    public string ReportContent { get; init; } = string.Empty;

    [StringLength(1000)]
    public string? ReportPath { get; init; }

    public int? PipelineArtifactId { get; init; }

    [StringLength(200)]
    public string? StageName { get; init; }

    [StringLength(200)]
    public string? StepName { get; init; }

    [StringLength(200)]
    public string? EnvironmentName { get; init; }

    [StringLength(128)]
    public string? RuleSetHash { get; init; }

    [StringLength(64)]
    public string? DastLeaseToken { get; init; }

    [StringLength(2048)]
    public string? DastTargetUrl { get; init; }

    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public bool IsTruncated { get; init; }

    [StringLength(2000)]
    public string? ErrorMessage { get; init; }
}

public sealed record AnalysisFindingPaginationRequest : PaginationRequest
{
    public AnalysisCategory? Category { get; init; }
    public AnalysisSeverity? Severity { get; init; }
    public AnalysisFindingStatus? Status { get; init; }

    /// <summary>Several categories ticked in the column filter; a finding matches any.</summary>
    [MaxLength(32)] public List<AnalysisCategory>? Categories { get; init; }

    /// <summary>Several severities; a finding matches any.</summary>
    [MaxLength(32)] public List<AnalysisSeverity>? Severities { get; init; }

    /// <summary>Several statuses; a finding matches any.</summary>
    [MaxLength(32)] public List<AnalysisFindingStatus>? Statuses { get; init; }

    public bool? IsNew { get; init; }

    /// <summary>The ID column's number filter as bounds: an inclusive lowest and highest identifier, and
    /// one identifier left out (its "not equal" operator). Each is optional.</summary>
    public int? IdFrom { get; init; }

    public int? IdTo { get; init; }

    public int? IdNot { get; init; }

    [StringLength(100)]
    public string? Scanner { get; init; }

    [StringLength(200)]
    public string? Branch { get; init; }

    [StringLength(200)]
    public string? Responsible { get; init; }
}

/// <summary> One page of the findings a set of pipeline runs observed (a run and the runs it
/// triggered, as its Gate tab merges them), most severe first. </summary>
public sealed record AnalysisRunFindingsRequest : PaginationRequest
{
    public const int MaxRuns = 50;

    [Required, MinLength(1), MaxLength(MaxRuns)]
    public List<int> RunIds { get; init; } = [];

    /// <summary>The findings already decided on (accepted, false positive, mitigated, fixed since)
    /// are left out unless asked for.</summary>
    public bool IncludeDecided { get; init; }

    /// <summary>The Gate tab's column filters; <see cref="PaginationRequest.Search"/> matches the title or the rule.</summary>
    [MaxLength(32)] public List<AnalysisCategory>? Categories { get; init; }

    [MaxLength(32)] public List<AnalysisSeverity>? Severities { get; init; }

    [MaxLength(32)] public List<AnalysisFindingStatus>? Statuses { get; init; }

    public bool? IsNew { get; init; }

    /// <summary>The ID column's number filter as bounds: an inclusive lowest and highest identifier, and
    /// one identifier left out (its "not equal" operator). Each is optional.</summary>
    public int? IdFrom { get; init; }

    public int? IdTo { get; init; }

    public int? IdNot { get; init; }
}

/// <summary>A page of run findings with the counts of the whole set, worked out by the database (a
/// finding observed by several runs of the set counts once).</summary>
public sealed record AnalysisRunFindingsPageDto
{
    public IReadOnlyList<AnalysisRunGateFindingDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int OpenCount { get; init; }
    public int NewOpenCount { get; init; }
    public int DecidedCount { get; init; }
}

public sealed record AnalysisPortfolioPaginationRequest : PaginationRequest
{
    public int? OrganizationId { get; init; }
    public int? ProjectId { get; init; }
    public int? PipelineId { get; init; }
    public AnalysisCategory? Category { get; init; }
    [StringLength(200)] public string? Branch { get; init; }
    [StringLength(64)] public string? Commit { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

public sealed record AnalysisPortfolioRowDto
{
    public int AnalysisReportId { get; init; }
    public int OrganizationId { get; init; }
    public string OrganizationName { get; init; } = string.Empty;
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public int? PipelineId { get; init; }
    public string? PipelineName { get; init; }
    public int? PipelineRunId { get; init; }
    public string? BranchName { get; init; }
    public string? CommitHash { get; init; }
    public AnalysisCategory Category { get; init; }
    public string ScannerName { get; init; } = string.Empty;
    public string ScannerVersion { get; init; } = string.Empty;
    public AnalysisReportStatus Status { get; init; }
    public AnalysisGateStatus? GateStatus { get; init; }
    public AnalysisGrade? Grade { get; init; }
    public AnalysisGradeCompleteness GradeCompleteness { get; init; }
    public int FindingCount { get; init; }
    public int NewFindingCount { get; init; }
    public int BlockerCount { get; init; }
    public int WarningCount { get; init; }
    public DateTime CompletedAt { get; init; }

    /// <summary>The internal repository the report's run built, when its clone URL names one, so
    /// the commit links to its Aetheus page; null otherwise (the commit is then text).</summary>
    public int? RepositoryId { get; init; }
}

public sealed record AnalysisPortfolioProjectDto
{
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public int OrganizationId { get; init; }
    public string OrganizationName { get; init; } = string.Empty;
    public int ReportCount { get; init; }
    public int OpenCount { get; init; }
    public int NewCount { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public DateTime? LastAnalysisAt { get; init; }
    public AnalysisGradeSummaryDto? Grade { get; init; }
}

public sealed record AnalysisProjectSummaryDto
{
    public int ProjectId { get; init; }
    public int OpenCount { get; init; }
    public int NewCount { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }
    public int AcceptedCount { get; init; }
    public int FixedCount { get; init; }
    public DateTime? LastAnalysisAt { get; init; }
    public AnalysisGradeSummaryDto? Grade { get; init; }

    /// <summary>The recorded commit the grade was measured on (its page), when the project's commit
    /// history holds it.</summary>
    public int? GradeCommitId { get; init; }

    /// <summary>The pipeline run of the project's latest analysis evaluation.</summary>
    public int? LastAnalysisRunId { get; init; }

    /// <summary>The project's latest release (published first, then detected).</summary>
    public int? LatestReleaseId { get; init; }
    [StringLength(200)] public string? LatestReleaseVersion { get; init; }
}

public sealed record AnalysisMetricPaginationRequest : PaginationRequest
{
    [StringLength(300)] public string? Key { get; init; }
    [StringLength(100)] public string? Language { get; init; }
    [StringLength(200)] public string? Branch { get; init; }
    public bool LatestReportOnly { get; init; }
}

public sealed record AnalysisComponentPaginationRequest : PaginationRequest
{
    [StringLength(100)] public string? ComponentType { get; init; }
    [StringLength(200)] public string? Branch { get; init; }
}

public sealed record AnalysisPolicyDto
{
    public int Id { get; init; }
    public int? OrganizationId { get; init; }
    public int? ProjectId { get; init; }
    public string PolicyKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public AnalysisCategory? Category { get; init; }
    public string? ScannerKey { get; init; }
    public string? RuleId { get; init; }
    public AnalysisSeverity? SeverityThreshold { get; init; }
    public bool NewFindingsOnly { get; init; }
    public string? MetricKey { get; init; }
    public AnalysisPolicyOperator? Operator { get; init; }
    public double? Threshold { get; init; }
    public string? BranchPattern { get; init; }
    public string? EnvironmentPattern { get; init; }
    public AnalysisGateBehavior Behavior { get; init; }
    public int Priority { get; init; }
    public bool Enabled { get; init; }
    public int Version { get; init; }
    public AnalysisPolicyScope Scope { get; init; }
    public bool IsInherited { get; init; }
    public bool IsOverride { get; init; }
    public bool IsEffective { get; init; } = true;
    public AnalysisPolicyScope? OverriddenScope { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record UpsertAnalysisPolicyRequest
{
    [StringLength(200)]
    [RegularExpression("^[a-z0-9]+(?:[.-][a-z0-9]+)*$")]
    public string? PolicyKey { get; init; }
    [Required, StringLength(200, MinimumLength = 3)]
    public string Name { get; init; } = string.Empty;
    public AnalysisCategory? Category { get; init; }
    [StringLength(100)] public string? ScannerKey { get; init; }
    [StringLength(300)] public string? RuleId { get; init; }
    public AnalysisSeverity? SeverityThreshold { get; init; }
    public bool NewFindingsOnly { get; init; }
    [StringLength(300)] public string? MetricKey { get; init; }
    public AnalysisPolicyOperator? Operator { get; init; }
    public double? Threshold { get; init; }
    [StringLength(300)] public string? BranchPattern { get; init; }
    [StringLength(300)] public string? EnvironmentPattern { get; init; }
    public AnalysisGateBehavior Behavior { get; init; }
    [Range(-10_000, 10_000)] public int Priority { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record AnalysisPolicyBatchItemRequest
{
    public int? PolicyId { get; init; }
    [Required]
    public UpsertAnalysisPolicyRequest Policy { get; init; } = new();
}

public sealed record ApplyAnalysisPolicyBatchRequest
{
    [Required, MinLength(1), MaxLength(100)]
    public List<AnalysisPolicyBatchItemRequest> Items { get; init; } = [];
}

public sealed record AnalysisPolicySetPreviewDto
{
    public int? OrganizationId { get; init; }
    public int? ProjectId { get; init; }
    public string SnapshotHash { get; init; } = string.Empty;
    public List<AnalysisPolicyDto> Policies { get; init; } = [];
    public List<string> ExpectedProducers { get; init; } = [];
    public List<string> Conflicts { get; init; } = [];
    public AnalysisRunGateDto? LatestRunGate { get; init; }
}

public sealed record PreviewAnalysisPolicySetRequest
{
    public int? PolicyId { get; init; }
    public UpsertAnalysisPolicyRequest? Candidate { get; init; }
}

public sealed record AnalysisPolicyRevisionDto
{
    public int PolicyId { get; init; }
    public int Version { get; init; }
    public string SnapshotHash { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

public sealed record AnalysisPolicyExceptionDto
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int? AnalysisPolicyId { get; init; }
    public int? AnalysisFindingId { get; init; }
    public string? Fingerprint { get; init; }
    public string? RuleId { get; init; }
    public string? ScannerKey { get; init; }
    public AnalysisCategory? Category { get; init; }
    public string? BranchPattern { get; init; }
    public string? EnvironmentPattern { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string CreatedByUsername { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public DateTime? RevokedAt { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record CreateAnalysisPolicyExceptionRequest
{
    public int? AnalysisPolicyId { get; init; }
    public int? AnalysisFindingId { get; init; }
    [StringLength(64)] public string? Fingerprint { get; init; }
    [StringLength(300)] public string? RuleId { get; init; }
    [StringLength(100)] public string? ScannerKey { get; init; }
    public AnalysisCategory? Category { get; init; }
    [StringLength(300)] public string? BranchPattern { get; init; }
    [StringLength(300)] public string? EnvironmentPattern { get; init; }
    [Required, StringLength(2000, MinimumLength = 10)] public string Reason { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
}

public sealed record AnalysisFindingDecisionDto
{
    public int Id { get; init; }
    public int AnalysisFindingId { get; init; }
    public AnalysisFindingStatus Status { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string CreatedByUsername { get; init; } = string.Empty;
    public DateTime? ExpiresAt { get; init; }
    public DateTime? RevokedAt { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record CreateAnalysisFindingDecisionRequest
{
    public AnalysisFindingStatus Status { get; init; }
    [Required, StringLength(2000, MinimumLength = 10)] public string Reason { get; init; } = string.Empty;
    public DateTime? ExpiresAt { get; init; }
}

public sealed record AnalysisTrackingStatusDto
{
    public int ProjectId { get; init; }
    public string Provider { get; init; } = string.Empty;
    public bool Active { get; init; }
    public string SyncStatus { get; init; } = string.Empty;
    public string? LastError { get; init; }
    public int LastKnownVulnerabilityCount { get; init; }
    public DateTime? LastSyncAt { get; init; }
}

public sealed record AnalysisVulnerabilityObservationDto
{
    public int Id { get; init; }
    public int? AnalysisReportId { get; init; }
    public string VulnerabilityId { get; init; } = string.Empty;
    public string ComponentName { get; init; } = string.Empty;
    public string ComponentVersion { get; init; } = string.Empty;
    public string? PackageUrl { get; init; }
    public AnalysisSeverity Severity { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool IsContinuous { get; init; }
    public DateTime ObservedAt { get; init; }
}
