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

    /// <summary>The policy outcome of a violation that rejects the gate (<c>AnalysisGateViolationParser</c>).</summary>
    private const string BlockOutcome = "Block";

    private const int MaxRulesNamed = 5;

    /// <summary>The blocking rules the run crossed: they, not the grade, reject a gate whose letter is good.</summary>
    public static bool RejectedByRules(AnalysisRunGateDto gate) =>
        gate.Status == AnalysisGateStatus.Blocked && gate.Violations.Any(violation => violation.Outcome == BlockOutcome);

    /// <summary>
    /// Recette R-17: the one sentence that says why a gate is rejected: the blocking rules it crossed, each with the
    /// number of findings or measures it caught; else the grade below the minimum; else that a blocking rule was
    /// crossed. Null for a gate that is not rejected. The gate letter alone never says it (it grades the required
    /// domains, and a rule can block whatever the letter is).
    /// </summary>
    public static string? RejectionReason(AnalysisRunGateDto gate, IStringLocalizer<AppStrings> localizer)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(localizer);
        if (gate.Status != AnalysisGateStatus.Blocked) return null;

        var rules = gate.Violations
            .Where(violation => violation.Outcome == BlockOutcome)
            .GroupBy(violation => violation.PolicyKey, StringComparer.Ordinal)
            // The rule by its human name; its key only when the policy has none to give.
            .Select(group => $"{group.Select(violation => violation.PolicyName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key} ({group.Sum(violation => Math.Max(1, violation.Count))})")
            .ToList();
        if (rules.Count > 0)
        {
            var named = string.Join(", ", rules.Take(MaxRulesNamed)) + (rules.Count > MaxRulesNamed ? ", …" : string.Empty);
            return string.Format(localizer["GateRejectedByRules"].Value, named);
        }

        if (gate.Grade is { OverallGrade: { } overall, MinimumGrade: { } minimum } && overall > minimum)
            return string.Format(localizer["GateRejectedByGrade"].Value, overall, minimum);
        return localizer["GateRejectedGeneric"].Value;
    }

    /// <summary>The gate uses the same grade as the run list, including the sealed candidate
    /// contract when applicable; see <c>PipelineRunGradeHydration</c>.</summary>
    public static AnalysisGrade? HeadlineGrade(AnalysisRunGateDto gate, AnalysisGrade? runGrade) =>
        runGrade ?? gate.Grade?.OverallGrade;

    /// <summary>True when the run's letter is not the analysis letter, so the gate says where it comes from.</summary>
    public static bool GradeComesFromSeal(AnalysisRunGateDto gate, AnalysisGrade? runGrade) =>
        runGrade is not null && runGrade != gate.Grade?.OverallGrade;

    /// <summary>The run parameter a deployment restores its candidate release by.</summary>
    internal const string CandidateVersionParameter = "candidateVersion";

    /// <summary>
    /// Recette R1-30: the Gate grade of the candidate a deploy run restored, for the status tile. A deploy
    /// run owns no analysis result, so the Quality tile is absent and the grade (completed by the API from the
    /// candidate release) was shown nowhere on the page. Null when the run is not a deployment of a candidate,
    /// when the API gave no grade, or when the Quality tile already carries the grade.
    /// </summary>
    public static AnalysisGrade? DeployedCandidateGrade(PipelineRunDto run, bool qualityTileShown) =>
        !qualityTileShown && run.Parameters.ContainsKey(CandidateVersionParameter) ? run.GateGrade : null;
}
