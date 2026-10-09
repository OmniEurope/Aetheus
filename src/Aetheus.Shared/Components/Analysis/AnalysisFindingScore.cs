// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>
/// Recette R2-119 (decided 2026-10-05, "au mieux, souple"): the weighted score of the open findings a
/// <c>finding_score: weighted</c> grading rule grades. Every open finding weighs by its severity (info
/// 0.5, low 1, medium 3, high 10), so five low findings never weigh as much as one high finding; a
/// critical finding has no weight because it alone makes the measure <see cref="AnalysisGrade.F"/>
/// (not deliverable), whatever the score. A finding without a severity weighs as an info one: an open
/// finding always costs the A.
/// </summary>
public static class AnalysisFindingScore
{
    /// <summary>The <c>finding_score</c> value of a grading rule that grades this score.</summary>
    public const string Weighted = "weighted";

    public const double InfoWeight = 0.5;
    public const double LowWeight = 1;
    public const double MediumWeight = 3;
    public const double HighWeight = 10;

    public static double Score(AnalysisGradeFindingCountsDto counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return counts.High * HighWeight
            + counts.Medium * MediumWeight
            + counts.Low * LowWeight
            + counts.Info * InfoWeight;
    }

    /// <summary>The counts of the given severities, one entry per distinct open finding.</summary>
    public static AnalysisGradeFindingCountsDto Count(IEnumerable<AnalysisSeverity> severities)
    {
        ArgumentNullException.ThrowIfNull(severities);
        var bySeverity = severities.GroupBy(severity => severity).ToDictionary(group => group.Key, group => group.Count());
        return new AnalysisGradeFindingCountsDto
        {
            Critical = bySeverity.GetValueOrDefault(AnalysisSeverity.Critical),
            High = bySeverity.GetValueOrDefault(AnalysisSeverity.High),
            Medium = bySeverity.GetValueOrDefault(AnalysisSeverity.Medium),
            Low = bySeverity.GetValueOrDefault(AnalysisSeverity.Low),
            Info = bySeverity.GetValueOrDefault(AnalysisSeverity.Info) + bySeverity.GetValueOrDefault(AnalysisSeverity.None)
        };
    }

    /// <summary>The counts of several evaluations of one run, each report counting its own findings.</summary>
    public static AnalysisGradeFindingCountsDto Sum(IEnumerable<AnalysisGradeFindingCountsDto> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var list = counts.ToList();
        return new AnalysisGradeFindingCountsDto
        {
            Critical = list.Sum(item => item.Critical),
            High = list.Sum(item => item.High),
            Medium = list.Sum(item => item.Medium),
            Low = list.Sum(item => item.Low),
            Info = list.Sum(item => item.Info)
        };
    }
}
