// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Analysis;

/// <summary>Why a gate verdict is <see cref="AnalysisGateStatus.Error"/>. Recette R-21: a verdict in error with no
/// finding and a good letter (run 3100) named nothing, so every condition that turns evidence into an error is a kind
/// here, each carried with the producer and the evidence it comes from.</summary>
public enum AnalysisGateErrorCauseKind
{
    /// <summary>The run holds no report of the scope: nothing was graded.</summary>
    NoReport = 0,
    /// <summary>The producer ended its report in <see cref="AnalysisReportStatus.Error"/> (its tool failed).</summary>
    ReportError = 1,
    /// <summary>The producer ended its report in <see cref="AnalysisReportStatus.TimedOut"/>.</summary>
    ReportTimedOut = 2,
    /// <summary>The producer ended its report in <see cref="AnalysisReportStatus.Unavailable"/> (tool or runtime missing).</summary>
    ReportUnavailable = 3,
    /// <summary>The report was stored without an evaluation: its policy verdict is unknown.</summary>
    NoEvaluation = 4,
    /// <summary>The report was evaluated in error although its own status is healthy.</summary>
    EvaluationError = 5,
    /// <summary>A task that was to publish a report published none.</summary>
    MissingProducer = 6,
    /// <summary>A DAST report nobody expected (a duplicate partition).</summary>
    UnexpectedReport = 7,
    /// <summary>The Dependency-Track upload of the SBOM is required and is not done (missing, pending or failed).</summary>
    DependencyTrack = 8,
    /// <summary>The grading measured nothing, so no letter exists.</summary>
    GradeNotComputed = 9
}

/// <summary>One reason of an Error verdict, with the producer (tool), the report or evidence and the precise reason.</summary>
public sealed record AnalysisGateErrorCauseDto
{
    public AnalysisGateErrorCauseKind Kind { get; init; }

    /// <summary>The run whose gate gave this cause (a child run when the gate merges a run tree).</summary>
    public int PipelineRunId { get; init; }

    /// <summary>The producer's scanner key (the tool), or null when the cause belongs to no single tool.</summary>
    public string? ScannerKey { get; init; }

    public string? ScannerName { get; init; }

    /// <summary>The pipeline step that produced, or should have produced, the evidence.</summary>
    public string? StepName { get; init; }

    public int? ReportId { get; init; }

    /// <summary>The raw report kept as an artifact of the run.</summary>
    public int? PipelineArtifactId { get; init; }

    /// <summary>The report file the producer read or wrote.</summary>
    public string? ReportPath { get; init; }

    /// <summary>The producer's own message (its error), or the state (<c>pending</c>, <c>failed</c>, <c>missing</c>) of the
    /// Dependency-Track upload.</summary>
    public string? Detail { get; init; }
}

/// <summary>Recette R-21: evidence the gate could not read (a policy or grade snapshot of a report that is not valid JSON).
/// It was dropped without a word, so a grade could rest on fewer reports than the run holds. It changes no verdict: it is
/// only named, with its report and its producer, in the gate log and summary.</summary>
public sealed record AnalysisGateUnreadableEvidenceDto
{
    public int ReportId { get; init; }
    public string? ScannerKey { get; init; }
    public string? StepName { get; init; }

    /// <summary>Which stored evidence is unreadable: <c>policy snapshot</c> or <c>grade snapshot</c>.</summary>
    public string Evidence { get; init; } = string.Empty;

    /// <summary>The parser's own message.</summary>
    public string? Detail { get; init; }
}
