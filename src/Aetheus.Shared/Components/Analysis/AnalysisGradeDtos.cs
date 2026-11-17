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

    /// <summary>True for a <c>finding_score: count</c> rule: <see cref="ObservedValue"/> is the
    /// number of open findings.</summary>
    public bool CountedFindingScore { get; init; }

    /// <summary>The open findings behind a finding score, by severity; null for any other rule.</summary>
    public AnalysisGradeFindingCountsDto? FindingCounts { get; init; }

    /// <summary>What this report adds to its run's finding score: <see cref="FindingCounts"/> without the findings an
    /// earlier report of the same run already counted (one finding seen by two scanners, or by the back and the front
    /// image, is one finding of the run). The run's grade sums these, so it counts the distinct findings the run lists
    /// (recette R-21). Null on snapshots written before it existed, and for rules that are not finding scores.</summary>
    public AnalysisGradeFindingCountsDto? RunFindingCounts { get; init; }

    /// <summary>The same for a severity rule: <see cref="ObservedValue"/> without the findings an earlier report of the
    /// run already counted. Null on older snapshots and for rules that do not count findings of a severity.</summary>
    public double? RunObservedValue { get; init; }

    /// <summary>The name of the pipeline step that publishes the evidence this measure reads (recette R-73,
    /// R-74), so a required measure nobody observed can say which step did not produce it. Null when the rule
    /// is not tied to one kind of evidence or the pipeline holds no such step.</summary>
    [StringLength(300)] public string? ProducerStep { get; init; }

    /// <summary>Why the pipeline's grading validator rejected this rule (or the whole <c>analysis_grading</c>
    /// block, under the key <c>analysis_grading</c>): such a measure is graded F, observed, so the grade and the
    /// gate say why instead of going silent. Null for a rule that was graded.</summary>
    [StringLength(2000)] public string? Rejection { get; init; }
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

/// <summary>R-7: the project's general "Findings" line: its open findings by severity and the grade they give
/// (<see cref="AnalysisFindingsGrade"/>). Counted in the project's overall grade.</summary>
public sealed record AnalysisGradeFindingsDto
{
    public AnalysisGrade Grade { get; init; }
    public AnalysisGradeFindingCountsDto Counts { get; init; } = new();
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

    /// <summary>Where <see cref="OverallGrade"/> comes from (R-12, R-20): the sealed assurance verdict of the
    /// release deployed in production. Null on a grade read straight from an analysis run (a run's own gate
    /// tab); set on the project grade, whose <see cref="Domains"/> stay the analysis grades as measured.
    /// <see cref="SnapshotHash"/> keeps hashing the analysis snapshot, not this qualification.</summary>
    public AnalysisQualificationDto? Qualification { get; init; }

    /// <summary>R-7: the general "Findings" line of the project grade, from its open findings. Null on a grade read
    /// from an analysis run (a run's own gate tab) and never written to a snapshot, so snapshot hashes do not move.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisGradeFindingsDto? Findings { get; init; }

    /// <summary>What lowers the project's overall grade below A (user decision 2026-11-10): the sealed verdict of a
    /// pipeline run (<see cref="AnalysisGradeCause.Pipeline"/>) or the project's own open findings
    /// (<see cref="AnalysisGradeCause.Project"/>). Null when the grade is A or has no letter. Like <see cref="Findings"/>,
    /// set on the project grade only and never written to a snapshot.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisGradeCause? Cause { get; init; }
}

/// <summary>What lowers a project's overall grade below A.</summary>
public enum AnalysisGradeCause
{
    /// <summary>The sealed verdict of a pipeline run (<see cref="AnalysisQualificationDto.PipelineRunId"/>).</summary>
    Pipeline = 1,

    /// <summary>The project itself: its open findings are worse than the sealed verdict.</summary>
    Project = 2
}

/// <summary>What the project's overall grade is worth (R-12, R-20).</summary>
public enum AnalysisQualificationState
{
    /// <summary>No release is deployed: the project has no qualified grade yet.</summary>
    NothingDeployed = 0,

    /// <summary>The deployed release carries the letter its assurance seal published.</summary>
    DeployedSealed = 1,

    /// <summary>The seal of the deployed release refused it (not deployable): the grade is F whatever the
    /// analysis domains say.</summary>
    DeployedNotDeployable = 2,

    /// <summary>A release is deployed but carries no sealed verdict (published outside the candidate
    /// pipeline): nothing qualifies it, so no letter is shown.</summary>
    DeployedWithoutVerdict = 3,

    /// <summary>The deployed release carries no sealed verdict, but an earlier release does (R-12, R-20, user
    /// option B): that last known letter is shown, marked old with its release and date, never as the deployed
    /// release's own grade.</summary>
    DeployedLastKnownGrade = 4
}

/// <summary>The deployed release an overall grade was read from, and its sealed verdict.</summary>
public sealed record AnalysisQualificationDto
{
    public AnalysisQualificationState State { get; init; }
    public int? ReleaseId { get; init; }

    /// <summary>The version a person reads for the release (application version when known).</summary>
    [StringLength(100)] public string? ReleaseVersion { get; init; }

    /// <summary>The run whose seal published the verdict (the one to open on its Gate tab).</summary>
    public int? PipelineRunId { get; init; }

    /// <summary>That run's number within its pipeline.</summary>
    public int? PipelineRunNumber { get; init; }

    /// <summary>The tests the seal counted as blocking; null when it published no count.</summary>
    public int? BlockingTestCount { get; init; }

    /// <summary>Only for <see cref="AnalysisQualificationState.DeployedLastKnownGrade"/>: the release whose old
    /// verdict the letter is (<see cref="ReleaseVersion"/> then names the deployed release without verdict, and
    /// <see cref="PipelineRunId"/> the run that sealed the old verdict).</summary>
    [StringLength(100)] public string? KnownReleaseVersion { get; init; }

    /// <summary>Only for <see cref="AnalysisQualificationState.DeployedLastKnownGrade"/>: when the release of the
    /// old verdict was published; null when unknown.</summary>
    public DateTime? KnownAt { get; init; }
}
