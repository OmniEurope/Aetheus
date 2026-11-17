// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>
/// Recette R-7: the grade of the project's general "Findings" line, computed from its open findings by severity.
/// The scale is the one the generic grading templates and the Aetheus pipelines already apply to findings
/// (ADR-030, owner decision of 2026-10-25): no open finding is an A; only medium, low or info findings are a B up to
/// 5 findings and a C from 6; at least one high finding is a D whatever the count; at least one critical finding is an
/// F (the same rule as <c>AnalysisGradeEngine</c> for a finding score). The ladder never goes below C for the
/// findings that are not high or critical, and E is never given.
/// </summary>
public static class AnalysisFindingsGrade
{
    /// <summary>The most medium, low and info findings that still grade B.</summary>
    public const int MaximumForB = 5;

    public static AnalysisGrade Of(AnalysisGradeFindingCountsDto counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Critical > 0) return AnalysisGrade.F;
        if (counts.High > 0) return AnalysisGrade.D;
        var total = AnalysisFindingScore.Total(counts);
        if (total == 0) return AnalysisGrade.A;
        return total <= MaximumForB ? AnalysisGrade.B : AnalysisGrade.C;
    }

    /// <summary>The line of a project: the counts and the grade they give.</summary>
    public static AnalysisGradeFindingsDto For(AnalysisGradeFindingCountsDto counts) =>
        new() { Counts = counts, Grade = Of(counts) };
}
