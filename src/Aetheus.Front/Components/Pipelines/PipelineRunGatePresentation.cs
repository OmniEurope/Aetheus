// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

internal static class PipelineRunGatePresentation
{
    public static OmniTone GateStyle(AnalysisGateStatus status) => status switch
    {
        AnalysisGateStatus.Passed => OmniTone.Success,
        AnalysisGateStatus.Warning => OmniTone.Warning,
        AnalysisGateStatus.Blocked or AnalysisGateStatus.Error => OmniTone.Danger,
        _ => OmniTone.Neutral
    };

    /// <summary>
    /// PLAN-003 lot 22: the status word is dropped when the grade letter carries the same verdict.
    /// The letter comes from the grade alone, so it can stay green on a gate blocked by a finding or
    /// in error for a missing report: for those two the word is always drawn.
    /// </summary>
    public static bool ShowsStatusWord(AnalysisRunGateDto gate) =>
        gate.Grade?.OverallGrade is null
        || gate.Status is AnalysisGateStatus.Blocked or AnalysisGateStatus.Error;

    public static string StatusKey(AnalysisGateStatus status) => $"AnalysisGateStatus{status}";

    /// <summary>
    /// Recette R2-129: the letter a run is listed with (its own evaluations, else the sealed contract a
    /// candidate published, see <c>PipelineRunGradeHydration</c>) is the letter its gate shows. Candidate
    /// #2571 was listed F (sealed: current E2E failed) while its gate showed the analysis A.
    /// </summary>
    public static AnalysisGrade? HeadlineGrade(AnalysisRunGateDto gate, AnalysisGrade? runGrade) =>
        runGrade ?? gate.Grade?.OverallGrade;

    /// <summary>True when the run's letter is not the analysis letter, so the gate says where it comes from.</summary>
    public static bool GradeComesFromSeal(AnalysisRunGateDto gate, AnalysisGrade? runGrade) =>
        runGrade is not null && runGrade != gate.Grade?.OverallGrade;
}
