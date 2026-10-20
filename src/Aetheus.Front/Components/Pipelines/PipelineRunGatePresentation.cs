// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

internal static class PipelineRunGatePresentation
{
    public static OmniTone GateStyle(AnalysisGateStatus status) => status switch
    {
        AnalysisGateStatus.Passed => OmniTone.Success,
        AnalysisGateStatus.Warning => OmniTone.Warning,
        // A gate a required control blocked reads "Rejeté" in warning; red stays for a gate in
        // error (a report missing, a technical failure).
        AnalysisGateStatus.Blocked => OmniTone.Warning,
        AnalysisGateStatus.Error => OmniTone.Danger,
        _ => OmniTone.Neutral
    };

    /// <summary> The status word is dropped when the grade letter carries the same verdict. The
    /// letter comes from the grade alone, so it can stay green on a gate blocked by a finding or in
    /// error for a missing report: for those two the word is always drawn. </summary>
    public static bool ShowsStatusWord(AnalysisRunGateDto gate) =>
        gate.Grade?.OverallGrade is null
        || gate.Status is AnalysisGateStatus.Blocked or AnalysisGateStatus.Error;

    public static string StatusKey(AnalysisGateStatus status) => $"AnalysisGateStatus{status}";

    /// <summary>The gate uses the same grade as the run list, including the sealed candidate
    /// contract when applicable; see <c>PipelineRunGradeHydration</c>.</summary>
    public static AnalysisGrade? HeadlineGrade(AnalysisRunGateDto gate, AnalysisGrade? runGrade) =>
        runGrade ?? gate.Grade?.OverallGrade;

    /// <summary>True when the run's letter is not the analysis letter, so the gate says where it comes from.</summary>
    public static bool GradeComesFromSeal(AnalysisRunGateDto gate, AnalysisGrade? runGrade) =>
        runGrade is not null && runGrade != gate.Grade?.OverallGrade;
}
