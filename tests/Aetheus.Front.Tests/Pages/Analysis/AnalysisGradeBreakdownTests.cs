// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>
/// Recette R2-119: a weighted finding score explains itself where rule values are shown: its label, the
/// score, and the open findings by severity with each weight.
/// </summary>
public sealed class AnalysisGradeBreakdownTests : BunitContext
{
    public AnalysisGradeBreakdownTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void AWeightedFindingScore_ShowsTheOpenFindingsBySeverityAndTheirWeights()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(
            component => component.Grade,
            Summary(AnalysisGrade.C, new AnalysisGradeFindingCountsDto { High = 1, Low = 2, Info = 1 }, 12.5)));

        var measure = Assert.Single(cut.FindAll(".analysis-grade-measure"));
        Assert.Contains("AnalysisOpenFindingsScore", measure.TextContent, StringComparison.Ordinal);
        Assert.Contains("12", measure.QuerySelector(".analysis-grade-value")!.TextContent, StringComparison.Ordinal);
        var hints = measure.QuerySelectorAll(".analysis-grade-measure-hint").Select(hint => hint.TextContent).ToList();
        var counts = Assert.Single(hints, hint => hint.Contains("Enum_AnalysisSeverity_High 1 (×10)", StringComparison.Ordinal));
        Assert.Contains("Enum_AnalysisSeverity_Critical 0", counts, StringComparison.Ordinal);
        Assert.Contains("Enum_AnalysisSeverity_Medium 0 (×3)", counts, StringComparison.Ordinal);
        Assert.Contains("Enum_AnalysisSeverity_Low 2 (×1)", counts, StringComparison.Ordinal);
        Assert.Matches(@"Enum_AnalysisSeverity_Info 1 \(×0[.,]5\)", counts);
        Assert.DoesNotContain(hints, hint => hint.Contains("AnalysisCriticalFindingNotDeliverable", StringComparison.Ordinal));
    }

    [Fact]
    public void ACriticalFinding_SaysItMakesTheGradeNotDeliverable()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(
            component => component.Grade,
            Summary(AnalysisGrade.F, new AnalysisGradeFindingCountsDto { Critical = 1 }, 0)));

        Assert.Contains(
            cut.FindAll(".analysis-grade-measure-hint"),
            hint => hint.TextContent.Contains("AnalysisCriticalFindingNotDeliverable", StringComparison.Ordinal));
    }

    private static AnalysisGradeSummaryDto Summary(
        AnalysisGrade grade, AnalysisGradeFindingCountsDto counts, double score) => new()
        {
            OverallGrade = grade,
            Completeness = AnalysisGradeCompleteness.Complete,
            LimitingDomain = AnalysisGradeDomain.Security,
            Domains =
        [
            new AnalysisGradeDomainDto
            {
                Domain = AnalysisGradeDomain.Security,
                Grade = grade,
                Required = true,
                Completeness = AnalysisGradeCompleteness.Complete,
                Measures =
                [
                    new AnalysisGradeMeasureDto
                    {
                        Key = "grade.security.findings",
                        Domain = AnalysisGradeDomain.Security,
                        Grade = grade,
                        Required = true,
                        Observed = true,
                        ObservedValue = score,
                        Direction = AnalysisMetricDirection.LowerIsBetter,
                        AThreshold = 0,
                        BThreshold = 5,
                        CThreshold = 15,
                        DThreshold = 30,
                        WeightedFindingScore = true,
                        FindingCounts = counts
                    }
                ]
            }
        ]
        };
}
