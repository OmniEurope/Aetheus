// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Analysis;

/// <summary>The release a project currently runs in production, with the verdict its assurance seal published.</summary>
internal sealed record DeployedRelease(
    int ProjectId,
    int ReleaseId,
    string Version,
    string? CommitHash,
    AnalysisGrade? AssuranceGrade,
    bool? Deployable,
    int? BlockingTestCount,
    int? PipelineRunId,
    int? PipelineRunNumber);

/// <summary>The newest sealed verdict of a release other than the deployed one (R-12, R-20, option B): what is
/// shown, marked old, when the deployed release carries none. <c>At</c> is when that release was published.</summary>
internal sealed record LastKnownVerdict(
    int ReleaseId,
    string Version,
    string? CommitHash,
    AnalysisGrade Grade,
    int? PipelineRunId,
    int? PipelineRunNumber,
    int? BlockingTestCount,
    DateTime? At);

/// <summary>
/// R-12, R-20: the project's overall grade is the qualification of the release deployed in production
/// (its sealed assurance verdict: <c>overallGrade</c>, and F when the seal refused it), never the grade of
/// the latest candidate or nightly run. The per-domain analysis grades stay as measured, beside it.
/// </summary>
internal static class AnalysisDeployedQualification
{
    /// <summary>The letter a sealed verdict publishes: F when the seal refused the release (not deployable),
    /// else the seal's own letter; null when the release carries no verdict.</summary>
    internal static AnalysisGrade? VerdictGrade(bool? deployable, AnalysisGrade? assuranceGrade) =>
        deployable == false ? AnalysisGrade.F : assuranceGrade;

    /// <summary>Qualifies the analysis grade (null when the project was never graded) with the deployed
    /// release (null when nothing is deployed). When the deployed release carries no sealed verdict, the
    /// <paramref name="lastKnown"/> verdict of an earlier release is shown instead, marked old (state
    /// <see cref="AnalysisQualificationState.DeployedLastKnownGrade"/>), never as the deployed release's own
    /// grade. Null when there is nothing to show at all.</summary>
    internal static AnalysisGradeSummaryDto? Qualify(
        AnalysisGradeSummaryDto? analysis, DeployedRelease? release, LastKnownVerdict? lastKnown = null)
    {
        if (release is null)
            return analysis is null
                ? null
                : analysis with
                {
                    OverallGrade = null,
                    LimitingDomain = null,
                    Qualification = new AnalysisQualificationDto { State = AnalysisQualificationState.NothingDeployed }
                };

        var (state, grade) = release switch
        {
            // The seal refused the release: F, whatever letter the analysis domains or the seal's own letter hold.
            { Deployable: false } => (AnalysisQualificationState.DeployedNotDeployable, VerdictGrade(false, null)),
            { AssuranceGrade: { } sealedGrade } => (AnalysisQualificationState.DeployedSealed, (AnalysisGrade?)sealedGrade),
            _ => (AnalysisQualificationState.DeployedWithoutVerdict, null)
        };
        if (state == AnalysisQualificationState.DeployedWithoutVerdict && lastKnown is not null)
            return QualifyWithLastKnown(analysis, release, lastKnown);
        if (analysis is null && state == AnalysisQualificationState.DeployedWithoutVerdict) return null;

        var basis = analysis ?? new AnalysisGradeSummaryDto();
        return basis with
        {
            OverallGrade = grade,
            // The limiting domain explains the analysis letter only; it does not explain another letter.
            LimitingDomain = grade == analysis?.OverallGrade ? analysis?.LimitingDomain : null,
            CommitHash = string.IsNullOrWhiteSpace(release.CommitHash) ? basis.CommitHash : release.CommitHash,
            PipelineRunId = release.PipelineRunId ?? basis.PipelineRunId,
            Qualification = new AnalysisQualificationDto
            {
                State = state,
                ReleaseId = release.ReleaseId,
                ReleaseVersion = release.Version,
                PipelineRunId = release.PipelineRunId,
                PipelineRunNumber = release.PipelineRunNumber,
                BlockingTestCount = release.BlockingTestCount
            }
        };
    }

    /// <summary>R-7: adds the general "Findings" line (the project's open findings, graded by
    /// <see cref="AnalysisFindingsGrade"/>) to a qualified grade and counts it in the overall letter the way every
    /// other line is: the overall letter is the worse of the qualification and the findings. A grade with no letter
    /// (nothing deployed, no verdict) stays without one, since a findings letter alone does not qualify a release; its
    /// Findings line is shown all the same. The limiting domain is dropped when the findings are what lowers the
    /// letter, since no domain explains it then.</summary>
    internal static AnalysisGradeSummaryDto WithFindings(AnalysisGradeSummaryDto qualified, AnalysisGradeFindingCountsDto openFindings)
    {
        var findings = AnalysisFindingsGrade.For(openFindings);
        var lowered = qualified.OverallGrade is { } overall && findings.Grade > overall;
        return qualified with
        {
            Findings = findings,
            OverallGrade = lowered ? findings.Grade : qualified.OverallGrade,
            LimitingDomain = lowered ? null : qualified.LimitingDomain,
            Cause = CauseOf(qualified, lowered)
        };
    }

    // User decision 2026-11-10: a grade below A names what lowers it. The project's findings when they are what sets the
    // letter; else the pipeline run whose seal published the verdict (none to name when that run is unknown).
    private static AnalysisGradeCause? CauseOf(AnalysisGradeSummaryDto qualified, bool loweredByFindings)
    {
        if (loweredByFindings) return AnalysisGradeCause.Project;
        if (qualified.OverallGrade is not { } grade || grade == AnalysisGrade.A) return null;
        return qualified.Qualification?.PipelineRunId is null ? null : AnalysisGradeCause.Pipeline;
    }

    // The deployed release has no verdict: the last known one is the letter, the deployed release only names the
    // state. The commit and run to open are the known verdict's, so the link leads to what produced the letter.
    private static AnalysisGradeSummaryDto QualifyWithLastKnown(
        AnalysisGradeSummaryDto? analysis, DeployedRelease release, LastKnownVerdict lastKnown)
    {
        var basis = analysis ?? new AnalysisGradeSummaryDto();
        return basis with
        {
            OverallGrade = lastKnown.Grade,
            LimitingDomain = null,
            CommitHash = string.IsNullOrWhiteSpace(lastKnown.CommitHash) ? basis.CommitHash : lastKnown.CommitHash,
            PipelineRunId = lastKnown.PipelineRunId ?? basis.PipelineRunId,
            Qualification = new AnalysisQualificationDto
            {
                State = AnalysisQualificationState.DeployedLastKnownGrade,
                ReleaseId = release.ReleaseId,
                ReleaseVersion = release.Version,
                PipelineRunId = lastKnown.PipelineRunId,
                PipelineRunNumber = lastKnown.PipelineRunNumber,
                BlockingTestCount = lastKnown.BlockingTestCount,
                KnownReleaseVersion = lastKnown.Version,
                KnownAt = lastKnown.At
            }
        };
    }
}
