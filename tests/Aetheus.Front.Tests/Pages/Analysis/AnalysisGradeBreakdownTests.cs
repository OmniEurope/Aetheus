// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>
/// Recette R1-4 (2026-10-09, "je veux pas de pondération, je montre le nombre"): a finding score shows
/// the number of open findings, the number the finding list shows, and no weighting text.
/// </summary>
public sealed class AnalysisGradeBreakdownTests : BunitContext
{
    public AnalysisGradeBreakdownTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void AFindingScore_ShowsTheNumberOfOpenFindings_WithoutWeights()
    {
        // Run 2653: 1 medium and 6 low findings showed "9" (the weighted score) over a list of 7.
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(
            component => component.Grade,
            Summary(AnalysisGrade.C, new AnalysisGradeFindingCountsDto { Medium = 1, Low = 6 }, 9, weighted: true)));

        var measure = Assert.Single(cut.FindAll(".analysis-grade-measure"));
        Assert.Contains("AnalysisOpenFindingCount", measure.TextContent, StringComparison.Ordinal);
        Assert.Equal("7", measure.QuerySelector(".analysis-grade-value")!.TextContent.Trim());
        Assert.DoesNotContain("×", measure.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum_AnalysisSeverity", measure.TextContent, StringComparison.Ordinal);
        // The distance of a weighted score is in weighted points, not in findings: not shown.
        Assert.DoesNotContain("AnalysisDistanceToGrade", measure.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountedFindingScore_ShowsTheNumberAndTheDistanceInFindings()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(
            component => component.Grade,
            Summary(AnalysisGrade.C, new AnalysisGradeFindingCountsDto { Medium = 1, Low = 6 }, 7)));

        var measure = Assert.Single(cut.FindAll(".analysis-grade-measure"));
        Assert.Equal("7", measure.QuerySelector(".analysis-grade-value")!.TextContent.Trim());
        Assert.Contains("AnalysisDistanceToGrade", measure.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_139_ASealBlockedByTests_BoldsTheCodeAndTestsDomain()
    {
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.A,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete },
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete }
            ]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, grade)
            .Add(component => component.SealBlockedByTests, true));

        var bold = Assert.Single(cut.FindAll(".analysis-grade-domain-limiting"));
        Assert.Equal("AnalysisCodeQualityAndTests", bold.TextContent.Trim());
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
        AnalysisGrade grade, AnalysisGradeFindingCountsDto counts, double score, bool weighted = false) => new()
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
                        WeightedFindingScore = weighted,
                        CountedFindingScore = !weighted,
                        NextGrade = grade > AnalysisGrade.A ? grade - 1 : null,
                        DistanceToNextGrade = grade > AnalysisGrade.A ? 2 : null,
                        FindingCounts = counts
                    }
                ]
            }
        ]
        };
}
