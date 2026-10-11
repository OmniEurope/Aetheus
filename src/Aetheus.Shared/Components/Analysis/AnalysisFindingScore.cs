// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>
/// The score of the open findings a <c>finding_score</c> grading rule grades, one entry per distinct
/// open finding. <c>count</c> (decided 2026-10-09, recette R1-4: "je veux pas de pondération, je montre
/// le nombre") grades the plain number of open findings, the number the run's finding list shows.
/// <c>weighted</c> (recette R2-119) weighs each finding by its severity (info 0.5, low 1, medium 3,
/// high 10). In both, a critical finding has no weight because it alone makes the measure
/// <see cref="AnalysisGrade.F"/> (not deliverable), whatever the score. A finding without a severity
/// counts as an info one.
/// </summary>
public static class AnalysisFindingScore
{
    /// <summary>The <c>finding_score</c> value of a rule that grades the severity-weighted score.</summary>
    public const string Weighted = "weighted";

    /// <summary>The <c>finding_score</c> value of a rule that grades the number of open findings.</summary>
    public const string Counted = "count";

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

    /// <summary>The number of open findings, every severity included.</summary>
    public static int Total(AnalysisGradeFindingCountsDto counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return counts.Critical + counts.High + counts.Medium + counts.Low + counts.Info;
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

    /// <summary>The value a finding-score measure grades: the number of findings, or the weighted score.</summary>
    public static double Value(AnalysisGradeFindingCountsDto counts, bool weighted) =>
        weighted ? Score(counts) : Total(counts);
}
