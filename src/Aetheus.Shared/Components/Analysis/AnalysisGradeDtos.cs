// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Analysis;

public sealed record AnalysisGradeMeasureDto
{
    [StringLength(200)] public string Key { get; init; } = string.Empty;
    public AnalysisGradeDomain Domain { get; init; }
    public AnalysisGrade? Grade { get; init; }
    public bool Required { get; init; }
    public bool Observed { get; init; }
    public double? ObservedValue { get; init; }
    [StringLength(300)] public string? MetricKey { get; init; }
    public AnalysisCategory? Category { get; init; }
    public AnalysisSeverity? Severity { get; init; }
    [StringLength(50)] public string? Unit { get; init; }
    public AnalysisMetricDirection Direction { get; init; }
    public double? AThreshold { get; init; }
    public double? BThreshold { get; init; }
    public double? CThreshold { get; init; }
    public double? DThreshold { get; init; }
    public double? EThreshold { get; init; }
    public AnalysisGrade? NextGrade { get; init; }
    public double? DistanceToNextGrade { get; init; }

    /// <summary>True for a <c>finding_score: weighted</c> rule: <see cref="ObservedValue"/> is the
    /// weighted score of the open findings (<see cref="AnalysisFindingScore"/>).</summary>
    public bool WeightedFindingScore { get; init; }

    /// <summary>True for a <c>finding_score: count</c> rule: <see cref="ObservedValue"/> is the number
    /// of open findings (recette R1-4).</summary>
    public bool CountedFindingScore { get; init; }

    /// <summary>The open findings behind a finding score, by severity; null for any other rule.</summary>
    public AnalysisGradeFindingCountsDto? FindingCounts { get; init; }
}

/// <summary>Distinct open findings by severity (an info count includes findings without a severity).</summary>
public sealed record AnalysisGradeFindingCountsDto
{
    public int Critical { get; init; }
    public int High { get; init; }
    public int Medium { get; init; }
    public int Low { get; init; }
    public int Info { get; init; }
}

public sealed record AnalysisGradeDomainDto
{
    public AnalysisGradeDomain Domain { get; init; }
    public AnalysisGrade? Grade { get; init; }
    public bool Required { get; init; }
    public AnalysisGradeCompleteness Completeness { get; init; }
    [StringLength(64)] public string? CommitHash { get; init; }
    public DateTime? EvaluatedAt { get; init; }
    public IReadOnlyList<AnalysisGradeMeasureDto> Measures { get; init; } = [];
}

public sealed record AnalysisGradeSummaryDto
{
    public int SchemaVersion { get; init; } = 1;
    public AnalysisGrade? OverallGrade { get; init; }
    public AnalysisGrade? MinimumGrade { get; init; }
    public AnalysisGradeCompleteness Completeness { get; init; }
    public AnalysisGradeDomain? LimitingDomain { get; init; }
    [StringLength(64)] public string SnapshotHash { get; init; } = string.Empty;
    [StringLength(64)] public string? CommitHash { get; init; }
    public int? PipelineRunId { get; init; }
    public DateTime? EvaluatedAt { get; init; }
    public IReadOnlyList<AnalysisGradeDomainDto> Domains { get; init; } = [];
}
