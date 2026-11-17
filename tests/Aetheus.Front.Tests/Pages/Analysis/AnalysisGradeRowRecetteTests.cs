// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>R-19 (a missing duplication measure says what to do) and R-20 (the CRAP average is a value, not a
/// grade: no "-" badge).</summary>
public sealed class AnalysisGradeRowRecetteTests : BunitContext
{
    public AnalysisGradeRowRecetteTests() => BunitTestHelper.RegisterServices(this);

    private static AnalysisGradeSummaryDto CodeQualityWith(AnalysisGradeMeasureDto measure) => new()
    {
        OverallGrade = AnalysisGrade.B,
        Domains =
        [
            new AnalysisGradeDomainDto
            {
                Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.B, Required = true,
                Completeness = AnalysisGradeCompleteness.Complete,
                Measures = [measure]
            }
        ]
    };

    private static AnalysisGradeMeasureDto Duplication(string? producerStep, string key = "grade.duplication") => new()
    {
        Key = key,
        Domain = AnalysisGradeDomain.CodeQuality,
        Required = true,
        Observed = false,
        ProducerStep = producerStep
    };

    [Theory]
    [InlineData("grade.duplication")]
    [InlineData("quality.duplication.percentage")]
    public void R19_AMissingDuplicationWithNoProducerStep_SaysToAddAJscpdStep(string key)
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, CodeQualityWith(Duplication(null, key)))
            .Add(component => component.GateGrade, AnalysisGrade.B));

        var hint = cut.Find("[data-testid='grade-row-measure-missing']");
        Assert.Contains("GradeMeasureDuplicationNoProducer", hint.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R19_AMissingDuplicationWithAProducerStep_StillNamesThatStep()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, CodeQualityWith(Duplication("Check duplication")))
            .Add(component => component.GateGrade, AnalysisGrade.B));

        var hint = cut.Find("[data-testid='grade-row-measure-missing']");
        Assert.Contains("Check duplication", hint.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("GradeMeasureDuplicationNoProducer", hint.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R20_TheCrapRow_ShowsItsValueWithoutAGradeBadge()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, CodeQualityWith(new AnalysisGradeMeasureDto
            {
                Key = "grade.coverage.line",
                Domain = AnalysisGradeDomain.CodeQuality,
                Required = true,
                Observed = true,
                Grade = AnalysisGrade.B
            }))
            .Add(component => component.GateGrade, AnalysisGrade.B)
            .Add(component => component.CrapScore, 12.34));

        var crap = cut.Find("[data-testid='grade-row-crap']");
        Assert.Contains("12", crap.TextContent, StringComparison.Ordinal);
        Assert.Empty(crap.QuerySelectorAll(".grade-badge"));
        // The other rows keep their badge.
        Assert.NotEmpty(cut.Find("[data-testid='grade-row-measure']").QuerySelectorAll(".grade-badge"));
    }
}
