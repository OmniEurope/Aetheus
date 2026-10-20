// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>Display open finding counts consistently with the findings list, without weighting
/// text.</summary>
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

    private static AnalysisGradeMeasureDto Measure(string key, double value, AnalysisGrade grade) => new()
    {
        Key = key,
        Observed = true,
        ObservedValue = value,
        Grade = grade
    };

    [Fact]
    public void TheFirstCause_IsTheFirstMeasureCarryingTheOverallGrade_InDisplayOrder()
    {
        // Recette R1-60: one cause beside the grade, chosen in a stable order, never a tie list.
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.C,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.Architecture, Grade = AnalysisGrade.C,
                    Measures = [Measure("grade.architecture.cycles", 46, AnalysisGrade.C)]
                },
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.C,
                    Measures = [Measure("grade.duplication", 3, AnalysisGrade.A), Measure("grade.complexity.maximum", 22, AnalysisGrade.C)]
                }
            ]
        };

        var cause = AnalysisGradeBreakdown.FirstLimitingMeasure(grade);

        Assert.Equal("grade.complexity.maximum", cause?.Key);
    }

    [Fact]
    public void AnAGrade_HasNoCause()
    {
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.A,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains = [new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.A }]
        };

        Assert.Null(AnalysisGradeBreakdown.FirstLimitingMeasure(grade));
    }

    [Theory]
    [InlineData("grade.security.critical", 0, false)]
    [InlineData("grade.architecture.cycles", 0, false)]
    [InlineData("grade.architecture.cycles", 2, true)]
    [InlineData("grade.coverage.line", 0, true)]
    [InlineData("grade.complexity.maximum", 0, true)]
    public void ACountAtZero_IsHidden_TheTwoKeptMeasuresAreNot(string key, double value, bool shown)
    {
        // Recette R1-61: zero counts say nothing; tested lines and the most complex path stay even at zero.
        Assert.Equal(shown, AnalysisGradeBreakdown.IsShown(Measure(key, value, AnalysisGrade.A)));
    }

    [Theory]
    [InlineData("grade.security.critical", "AnalysisCriticalVulnerabilities")]
    [InlineData("grade.security.high", "AnalysisHighVulnerabilities")]
    [InlineData("grade.security.medium", "AnalysisMediumVulnerabilities")]
    [InlineData("grade.security.findings", "AnalysisOpenFindingCount")]
    [InlineData("grade.quality.findings", "AnalysisOpenFindingCount")]
    [InlineData("grade.coverage.line", "AnalysisTestedLines")]
    [InlineData("grade.coverage.branch", "AnalysisTestedBranches")]
    [InlineData("grade.complexity.maximum", "AnalysisMaximumComplexity")]
    [InlineData("grade.duplication", "AnalysisDuplicatedCode")]
    [InlineData("grade.architecture.cycles", "AnalysisDependencyCycles")]
    public void MeasureLabel_AKnownMeasure_UsesItsLocalizedLabel(string key, string resource) =>
        Assert.Equal(resource, AnalysisGradeBreakdown.MeasureLabel(new BunitTestHelper.StubLocalizer(), key));

    [Theory]
    [InlineData("grade.new.measure", "new measure")]
    [InlineData("GRADE.Other.Thing", "Other Thing")]
    [InlineData("coverage.line.percent", "coverage.line.percent")]
    public void MeasureLabel_AnUnknownMeasure_IsReadableWithoutItsGradePrefix(string key, string expected) =>
        Assert.Equal(expected, AnalysisGradeBreakdown.MeasureLabel(new BunitTestHelper.StubLocalizer(), key));

    [Theory]
    [InlineData(AnalysisGradeDomain.Security, "AnalysisApplicationSecurity")]
    [InlineData(AnalysisGradeDomain.Reliability, "AnalysisCodeQualityAndTests")]
    [InlineData(AnalysisGradeDomain.CodeQuality, "AnalysisCodeQualityAndTests")]
    [InlineData(AnalysisGradeDomain.Architecture, "AnalysisCodeArchitecture")]
    [InlineData(AnalysisGradeDomain.Performance, "AnalysisRuntimePerformance")]
    [InlineData(null, "NotAvailable")]
    public void DomainLabel_EachDomain_UsesItsLocalizedLabel(AnalysisGradeDomain? domain, string resource) =>
        Assert.Equal(resource, AnalysisGradeBreakdown.DomainLabel(new BunitTestHelper.StubLocalizer(), domain));

    [Fact]
    public void R2_139_DomainsTiedOnTheOverallLetter_AreAllBold()
    {
        // Every domain that sets the overall letter is bold, not only the first one.
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.C,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.C, Completeness = AnalysisGradeCompleteness.Complete },
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.B, Completeness = AnalysisGradeCompleteness.Complete },
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Architecture, Grade = AnalysisGrade.C, Completeness = AnalysisGradeCompleteness.Complete }
            ]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(component => component.Grade, grade));

        Assert.Equal(
            ["AnalysisApplicationSecurity", "AnalysisCodeArchitecture"],
            cut.FindAll(".analysis-grade-domain-limiting").Select(element => element.TextContent.Trim()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void R2_141_ACompleteDomain_HasNoRequiredCompleteFreshnessLine_NorSeverityCounts()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(
            component => component.Grade,
            Summary(AnalysisGrade.C, new AnalysisGradeFindingCountsDto { High = 1, Medium = 2, Low = 3, Info = 1 }, 7)));

        Assert.Empty(cut.FindAll(".analysis-grade-domain-state"));
        foreach (var removed in new[] { "AnalysisRequired", "AnalysisAdvisory", "AnalysisFreshness", "Complete", "Enum_AnalysisSeverity" })
            Assert.DoesNotContain(removed, cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void R2_142_ThePerformanceDomain_ShowsOnlyOnceAMeasureIsObserved(bool observed, bool shown)
    {
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.A,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete },
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.Performance,
                    Grade = AnalysisGrade.A,
                    Completeness = AnalysisGradeCompleteness.Complete,
                    Measures = [new AnalysisGradeMeasureDto { Key = "grade.performance.p95", Domain = AnalysisGradeDomain.Performance, Observed = observed, ObservedValue = observed ? 120 : null, Grade = AnalysisGrade.A }]
                }
            ]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(component => component.Grade, grade));

        Assert.Equal(shown, cut.Markup.Contains("AnalysisRuntimePerformance", StringComparison.Ordinal));
    }
}
