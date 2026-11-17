// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>R-7: the scale of the project's general Findings line is the one the pipelines already apply to findings
/// (ADR-030): none is an A, medium, low and info only a B up to 5 and a C from 6, a high one a D, a critical one an F.</summary>
public sealed class AnalysisFindingsGradeTests
{
    private static AnalysisGradeFindingCountsDto Counts(int critical = 0, int high = 0, int medium = 0, int low = 0, int info = 0) =>
        new() { Critical = critical, High = high, Medium = medium, Low = low, Info = info };

    [Theory]
    [InlineData(0, 0, 0, 0, 0, AnalysisGrade.A)]
    [InlineData(0, 0, 1, 0, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 0, 0, 1, AnalysisGrade.B)]
    [InlineData(0, 0, 2, 2, 1, AnalysisGrade.B)]
    [InlineData(0, 0, 3, 2, 1, AnalysisGrade.C)]
    [InlineData(0, 0, 0, 40, 0, AnalysisGrade.C)]
    [InlineData(0, 1, 0, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 1, 30, 30, 30, AnalysisGrade.D)]
    [InlineData(0, 25, 0, 0, 0, AnalysisGrade.D)]
    [InlineData(1, 0, 0, 0, 0, AnalysisGrade.F)]
    [InlineData(1, 4, 9, 9, 9, AnalysisGrade.F)]
    public void TheScale_FollowsTheSeverityLadder(int critical, int high, int medium, int low, int info, AnalysisGrade expected) =>
        Assert.Equal(expected, AnalysisFindingsGrade.Of(Counts(critical, high, medium, low, info)));

    [Fact]
    public void WithFindings_LowersTheLetterAndKeepsAnEmptyLetterEmpty()
    {
        var sealedA = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A, LimitingDomain = AnalysisGradeDomain.Security };
        var lowered = AnalysisDeployedQualification.WithFindings(sealedA, Counts(high: 2));
        Assert.Equal(AnalysisGrade.D, lowered.OverallGrade);
        Assert.Equal(AnalysisGrade.D, lowered.Findings!.Grade);
        Assert.Null(lowered.LimitingDomain);

        // A letter already worse than the findings stays, with its limiting domain.
        var sealedF = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.F, LimitingDomain = AnalysisGradeDomain.Security };
        var kept = AnalysisDeployedQualification.WithFindings(sealedF, Counts(medium: 1));
        Assert.Equal(AnalysisGrade.F, kept.OverallGrade);
        Assert.Equal(AnalysisGradeDomain.Security, kept.LimitingDomain);

        // No letter (nothing deployed): the findings do not invent one, the line is there all the same.
        var none = AnalysisDeployedQualification.WithFindings(new AnalysisGradeSummaryDto(), Counts(critical: 1));
        Assert.Null(none.OverallGrade);
        Assert.Equal(AnalysisGrade.F, none.Findings!.Grade);
    }
}
