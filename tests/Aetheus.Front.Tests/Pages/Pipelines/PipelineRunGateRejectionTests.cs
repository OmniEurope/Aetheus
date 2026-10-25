// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-17: a rejected Gate says why in one sentence, whatever its letter.</summary>
public sealed class PipelineRunGateRejectionTests
{
    private static readonly IStringLocalizer<Aetheus.Front.Resources.AppStrings> Localizer = new FormatLocalizer(
        new Dictionary<string, string>
        {
            ["GateRejectedByRules"] = "rules:{0}",
            ["GateRejectedByGrade"] = "grade:{0}<{1}",
            ["GateRejectedGeneric"] = "generic"
        });

    private static AnalysisRunGateViolationDto Violation(string policy, int count, string outcome = "Block") => new()
    {
        PolicyKey = policy,
        Outcome = outcome,
        Count = count
    };

    [Fact]
    public void ARejectedGate_NamesTheBlockingRules_WithWhatEachCaught_AndNotTheWarnOnes()
    {
        // Gate #2864 of w5-candidate: letter A, rejected by blocking rules (a coverage threshold, cycles, a SAST rule).
        var gate = new AnalysisRunGateDto
        {
            Status = AnalysisGateStatus.Blocked,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A, MinimumGrade = AnalysisGrade.F },
            Violations =
            [
                Violation("coverage.line.minimum", 1),
                Violation("architecture.cycles", 3),
                Violation("coverage.line.minimum", 1),
                Violation("sast.medium", 1, outcome: "Warn")
            ]
        };

        Assert.Equal("rules:coverage.line.minimum (2), architecture.cycles (3)", PipelineRunGatePresentation.RejectionReason(gate, Localizer));
        Assert.True(PipelineRunGatePresentation.RejectedByRules(gate));
    }

    [Fact]
    public void ARejectedGateWithoutBlockingRule_SaysTheGradeIsBelowTheMinimum()
    {
        var gate = new AnalysisRunGateDto
        {
            Status = AnalysisGateStatus.Blocked,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.D, MinimumGrade = AnalysisGrade.B }
        };

        Assert.Equal("grade:D<B", PipelineRunGatePresentation.RejectionReason(gate, Localizer));
        Assert.False(PipelineRunGatePresentation.RejectedByRules(gate));
    }

    [Fact]
    public void ARejectedGateWithNoDetail_StillSaysABlockingRuleWasCrossed()
    {
        var gate = new AnalysisRunGateDto { Status = AnalysisGateStatus.Blocked, BlockerCount = 2 };

        Assert.Equal("generic", PipelineRunGatePresentation.RejectionReason(gate, Localizer));
    }

    [Theory]
    [InlineData(AnalysisGateStatus.Passed)]
    [InlineData(AnalysisGateStatus.Warning)]
    [InlineData(AnalysisGateStatus.Error)]
    public void AGateThatIsNotRejected_HasNoRejectionSentence(AnalysisGateStatus status)
    {
        var gate = new AnalysisRunGateDto { Status = status, Violations = [Violation("coverage.line.minimum", 1)] };

        Assert.Null(PipelineRunGatePresentation.RejectionReason(gate, Localizer));
        Assert.False(PipelineRunGatePresentation.RejectedByRules(gate));
    }

    [Fact]
    public void TheBlockingRules_AreNamedByTheirHumanName_AndByTheirKeyOnlyWhenTheyHaveNone()
    {
        var gate = new AnalysisRunGateDto
        {
            Status = AnalysisGateStatus.Blocked,
            Violations =
            [
                Violation("coverage.line.minimum", 1) with { PolicyName = "Couverture de lignes minimale" },
                Violation("coverage.line.minimum", 1) with { PolicyName = "Couverture de lignes minimale" },
                Violation("architecture.cycles", 3) with { PolicyName = "  " },
                Violation("sast.high", 1)
            ]
        };

        Assert.Equal(
            "rules:Couverture de lignes minimale (2), architecture.cycles (3), sast.high (1)",
            PipelineRunGatePresentation.RejectionReason(gate, Localizer));
    }

    [Fact]
    public void MoreThanFiveRules_AreCut_WithAnEllipsis()
    {
        var gate = new AnalysisRunGateDto
        {
            Status = AnalysisGateStatus.Blocked,
            Violations = [.. Enumerable.Range(1, 7).Select(index => Violation($"rule.{index}", 1))]
        };

        Assert.Equal("rules:rule.1 (1), rule.2 (1), rule.3 (1), rule.4 (1), rule.5 (1), …", PipelineRunGatePresentation.RejectionReason(gate, Localizer));
    }

    private sealed class FormatLocalizer(IReadOnlyDictionary<string, string> values) : IStringLocalizer<Aetheus.Front.Resources.AppStrings>
    {
        public LocalizedString this[string name] => new(name, values.TryGetValue(name, out var value) ? value : name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(this[name].Value, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
