// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>R1-36 / ADR-053: a metric rule the analysis crosses is a finding to fix, tracked across runs.</summary>
public sealed class AnalysisThresholdFindingsTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CoverageUnderItsMinimum_ProducesOneMediumFindingNamingTheRule()
    {
        var report = CoverageReport("Back", 70);

        var outcome = AnalysisThresholdEvaluator.Evaluate(report, Context(), SystemPolicies());

        var finding = Assert.Single(outcome.Crossed);
        Assert.Equal(AnalysisSeverity.Medium, finding.Severity);
        Assert.Equal(AnalysisCategory.Coverage, finding.Category);
        Assert.Equal("threshold.metric.quality.coverage.line", finding.RuleId);
        Assert.Contains("coverage.line.percent", finding.Message, StringComparison.Ordinal);
        Assert.Contains("70", finding.Message, StringComparison.Ordinal);
        Assert.Contains("80", finding.Message, StringComparison.Ordinal);
        Assert.Empty(outcome.NoLongerCrossed.Intersect([finding.Fingerprint]));
    }

    [Fact]
    public void TheFingerprint_IsTheRuleAndTheModule_NeverTheValue()
    {
        var first = Assert.Single(AnalysisThresholdEvaluator.Evaluate(
            CoverageReport("Back", 70), Context(), SystemPolicies()).Crossed);
        var worse = Assert.Single(AnalysisThresholdEvaluator.Evaluate(
            CoverageReport("Back", 41.5), Context(), SystemPolicies()).Crossed);
        var otherModule = Assert.Single(AnalysisThresholdEvaluator.Evaluate(
            CoverageReport("Front", 70), Context(), SystemPolicies()).Crossed);

        Assert.Equal(first.Fingerprint, worse.Fingerprint);
        Assert.NotEqual(first.Fingerprint, otherModule.Fingerprint);
        Assert.NotEqual(first.Message, worse.Message);
    }

    [Fact]
    public void ARuleNoLongerCrossed_IsListedToBeClosed_UnderTheSameIdentityAsWhenItWasOpened()
    {
        var crossed = Assert.Single(AnalysisThresholdEvaluator.Evaluate(
            CoverageReport("Back", 70), Context(), SystemPolicies()).Crossed);

        var outcome = AnalysisThresholdEvaluator.Evaluate(CoverageReport("Back", 91), Context(), SystemPolicies());

        Assert.Empty(outcome.Crossed);
        Assert.Contains(crossed.Fingerprint, outcome.NoLongerCrossed);
    }

    [Fact]
    public void AMetricAboutOneFileOrOneSymbol_ProducesNoFinding()
    {
        var report = CoverageReport("Back", 10);
        report.Metrics.Single().FilePath = "src/A.cs";

        var outcome = AnalysisThresholdEvaluator.Evaluate(report, Context(), SystemPolicies());

        Assert.Empty(outcome.Crossed);
        Assert.Empty(outcome.NoLongerCrossed);
    }

    [Fact]
    public void ADegradation_ProducesAFindingOnlyWhenTheMeasureGotWorse()
    {
        var worse = Report(AnalysisCategory.CodeQuality, "roslyn-metrics", "Quality",
            Metric("custom.maintainability", 79, AnalysisMetricDirection.HigherIsBetter, baseline: 80));
        var same = Report(AnalysisCategory.CodeQuality, "roslyn-metrics", "Quality",
            Metric("custom.maintainability", 80, AnalysisMetricDirection.HigherIsBetter, baseline: 80));

        var crossed = Assert.Single(AnalysisThresholdEvaluator.Evaluate(worse, Context(), SystemPolicies()).Crossed);
        var quiet = AnalysisThresholdEvaluator.Evaluate(same, Context(), SystemPolicies());

        Assert.Contains("got worse", crossed.Title, StringComparison.Ordinal);
        Assert.Contains("80", crossed.Message, StringComparison.Ordinal);
        Assert.Empty(quiet.Crossed);
        Assert.Contains(crossed.Fingerprint, quiet.NoLongerCrossed);
    }

    [Fact]
    public void ADependencyCycleCount_ProducesNoFindingHere_TheAgentPublishesTheGroups()
    {
        var report = Report(AnalysisCategory.Architecture, "dotnet-architecture-metrics", "arch",
            Metric("architecture.cycles.type", 40, AnalysisMetricDirection.LowerIsBetter));

        var outcome = AnalysisThresholdEvaluator.Evaluate(report, Context(), SystemPolicies());

        Assert.Empty(outcome.Crossed);
    }

    [Theory]
    [InlineData(AnalysisFindingStatus.Accepted, true)]
    [InlineData(AnalysisFindingStatus.FalsePositive, true)]
    [InlineData(AnalysisFindingStatus.Mitigated, true)]
    [InlineData(AnalysisFindingStatus.Open, false)]
    [InlineData(AnalysisFindingStatus.Fixed, false)]
    public async Task ADecidedCrossing_NoLongerWarns_AndAnOpenOrFixedOneStillDoesOnce(
        AnalysisFindingStatus status, bool decided)
    {
        var report = CoverageReport("Back", 70);
        var policies = SystemPolicies();
        var crossed = Assert.Single(AnalysisThresholdEvaluator.Evaluate(report, Context(), policies).Crossed);
        report.Occurrences.Add(new AnalysisFindingOccurrence
        {
            RuleId = crossed.RuleId,
            IsNew = true,
            AnalysisFinding = new AnalysisFinding
            {
                Id = 9,
                Fingerprint = crossed.Fingerprint,
                Category = crossed.Category,
                Severity = crossed.Severity,
                Status = status
            }
        });

        var evaluation = await new AnalysisPolicyEngine(Repository()).EvaluateAsync(
            report, Context(), Now, TestContext.Current.CancellationToken);

        // The metric rule counts the crossing once; the finding it produced is not counted a second time by
        // the "new medium coverage finding" policy. A decision lifts the alert altogether.
        Assert.Equal(decided ? 0 : 1, evaluation.WarningCount);
        Assert.Equal(decided ? AnalysisGateStatus.Passed : AnalysisGateStatus.Warning, evaluation.Status);
    }

    [Fact]
    public void TheCrossedFindingsOfTwoReportsOfOneScanner_DoNotShareAnIdentity()
    {
        var back = Assert.Single(AnalysisThresholdEvaluator.Evaluate(
            CoverageReport("Back", 70), Context(), SystemPolicies()).Crossed);
        var front = Assert.Single(AnalysisThresholdEvaluator.Evaluate(
            CoverageReport("Front", 70), Context(), SystemPolicies()).Crossed);

        Assert.NotEqual(back.Fingerprint, front.Fingerprint);
        Assert.NotEqual(back.LocationHash, front.LocationHash);
    }

    private static AnalysisReport CoverageReport(string module, double value) =>
        Report(AnalysisCategory.Coverage, "coverlet", module,
            Metric("coverage.line.percent", value, AnalysisMetricDirection.HigherIsBetter));

    private static AnalysisReport Report(AnalysisCategory category, string scanner, string module, AnalysisMetric metric)
    {
        var report = new AnalysisReport
        {
            Id = 5,
            Category = category,
            ScannerKey = scanner,
            StepName = module,
            Status = AnalysisReportStatus.Passed
        };
        report.Metrics.Add(metric);
        return report;
    }

    private static AnalysisMetric Metric(
        string key, double value, AnalysisMetricDirection direction, double? baseline = null) => new()
        {
            Key = key,
            Value = value,
            Unit = "percent",
            Scope = "project",
            Language = "csharp",
            Direction = direction,
            BaselineValue = baseline,
            ToolName = "test"
        };

    private static List<ResolvedAnalysisPolicy> SystemPolicies() =>
        AnalysisPolicyResolver.Resolve(1, 2, []);

    private static IAnalysisRepository Repository()
    {
        var repository = Substitute.For<IAnalysisRepository>();
        repository.GetApplicablePoliciesAsync(1, 2, Arg.Any<CancellationToken>()).Returns([]);
        repository.GetActiveExceptionsAsync(2, Now, Arg.Any<CancellationToken>()).Returns([]);
        return repository;
    }

    private static AnalysisRunContext Context() =>
        new(1, 2, 3, "feature", new string('a', 40), "main", PipelineYaml: "");
}
