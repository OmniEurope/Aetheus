// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

public sealed class AnalysisPolicyEngineTests
{
    private static readonly DateTime Now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EvaluateAsync_NewHighSecurityFindingWarns()
    {
        var evaluation = await EvaluateAsync(AnalysisCategory.Sast, Finding(AnalysisSeverity.High, isNew: true));

        Assert.Equal(AnalysisGateStatus.Warning, evaluation.Status);
        Assert.Equal(0, evaluation.BlockerCount);
        Assert.Equal(1, evaluation.WarningCount);
    }

    [Fact]
    public async Task EvaluateAsync_BaselinedHighSecurityFindingDoesNotBlock()
    {
        var evaluation = await EvaluateAsync(AnalysisCategory.Sast, Finding(AnalysisSeverity.High, isNew: false));

        Assert.Equal(AnalysisGateStatus.Passed, evaluation.Status);
        Assert.Equal(0, evaluation.BlockerCount);
    }

    [Theory]
    [InlineData("coverage.line.percent", 74.9, AnalysisMetricDirection.HigherIsBetter, AnalysisGateStatus.Warning)]
    [InlineData("duplication.percentage", 5.1, AnalysisMetricDirection.LowerIsBetter, AnalysisGateStatus.Warning)]
    [InlineData("complexity.cyclomatic.average", 10.1, AnalysisMetricDirection.LowerIsBetter, AnalysisGateStatus.Warning)]
    [InlineData("complexity.cyclomatic.maximum", 25.1, AnalysisMetricDirection.LowerIsBetter, AnalysisGateStatus.Warning)]
    public async Task EvaluateAsync_DefaultAbsoluteQualityThresholdAppliesVersionedBehavior(
        string key,
        double value,
        AnalysisMetricDirection direction,
        AnalysisGateStatus expectedStatus)
    {
        var evaluation = await EvaluateAsync(AnalysisCategory.CodeQuality, Metric(key, value, direction));

        Assert.Equal(expectedStatus, evaluation.Status);
        Assert.Equal(expectedStatus == AnalysisGateStatus.Warning ? 1 : 0, evaluation.WarningCount);
        Assert.Equal(expectedStatus == AnalysisGateStatus.Blocked ? 1 : 0, evaluation.BlockerCount);
    }

    [Theory]
    [InlineData("architecture.cycles")]
    [InlineData("architecture.cycles.type")]
    [InlineData("architecture.cycles.namespace")]
    public async Task EvaluateAsync_ACycleCountIsNoLongerAThresholdAlert(string key)
    {
        // R1-36 / ADR-053: a group of cycles is a finding of its own (published by the agent with its
        // members), so the count of cycles has no rule of its own and alerts nothing.
        var evaluation = await EvaluateAsync(
            AnalysisCategory.Architecture, Metric(key, 40, AnalysisMetricDirection.LowerIsBetter));

        Assert.Equal(AnalysisGateStatus.Passed, evaluation.Status);
        Assert.Equal(0, evaluation.WarningCount);
        Assert.Equal(0, evaluation.BlockerCount);
    }

    [Fact]
    public void TheDefaultCatalog_HasNoDependencyCycleRule()
    {
        Assert.DoesNotContain(
            AnalysisDefaultPolicyCatalog.Policies,
            policy => policy.PolicyKey == "quality.architecture.cycles"
                || policy.MetricKey?.StartsWith("architecture.cycles", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task EvaluateAsync_ComplexMethodsAboveThePreviousCountWarnsOnceAndNamesBothNumbers()
    {
        var metric = Metric("complexity.methods.high", 319, AnalysisMetricDirection.LowerIsBetter);
        metric.BaselineValue = 318;
        var report = Report(AnalysisCategory.CodeQuality, metric);

        var evaluation = await new AnalysisPolicyEngine(Repository()).EvaluateAsync(
            report, Context(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Warning, evaluation.Status);
        // One alert: the policy naming the metric replaces the catch-all degradation one.
        Assert.Equal(1, evaluation.WarningCount);
        var violation = Assert.Single(AnalysisGateViolationParser.Parse(
            [new AnalysisEvaluationSnapshot(report.Id, evaluation.PolicySnapshotJson)]));
        Assert.Equal("quality.complexity.high-methods", violation.PolicyKey);
        Assert.Equal("Degraded", violation.Operator);
        Assert.Equal("319", violation.ObservedValue);
        Assert.Equal("318", violation.Threshold);
    }

    [Theory]
    [InlineData(318, 318)]
    [InlineData(300, 318)]
    [InlineData(0, 318)]
    public async Task EvaluateAsync_ComplexMethodsEqualOrBelowThePreviousCountDoNotAlert(double value, double baseline)
    {
        var metric = Metric("complexity.methods.high", value, AnalysisMetricDirection.LowerIsBetter);
        metric.BaselineValue = baseline;

        var evaluation = await EvaluateAsync(AnalysisCategory.CodeQuality, metric);

        Assert.Equal(AnalysisGateStatus.Passed, evaluation.Status);
        Assert.Equal(0, evaluation.WarningCount);
    }

    [Fact]
    public async Task EvaluateAsync_ComplexMethodsWithoutAPreviousAnalysisDoNotAlert()
    {
        // The first analysis has nothing to be worse than: its count becomes the baseline.
        var evaluation = await EvaluateAsync(
            AnalysisCategory.CodeQuality,
            Metric("complexity.methods.high", 318, AnalysisMetricDirection.LowerIsBetter));

        Assert.Equal(AnalysisGateStatus.Passed, evaluation.Status);
        Assert.Equal(0, evaluation.WarningCount);
    }

    [Fact]
    public async Task EvaluateAsync_QualityDegradationWarnsWithoutExplicitPolicy()
    {
        var metric = Metric("custom.maintainability", 79, AnalysisMetricDirection.HigherIsBetter);
        metric.BaselineValue = 80;

        var evaluation = await EvaluateAsync(AnalysisCategory.CodeQuality, metric);

        Assert.Equal(AnalysisGateStatus.Warning, evaluation.Status);
        Assert.Equal(0, evaluation.BlockerCount);
    }

    [Fact]
    public async Task EvaluateAsync_ExplicitQualityPolicyCanBlock()
    {
        var repository = Repository();
        var policy = BlockingMetricPolicy("coverage.line.percent", AnalysisPolicyOperator.LessThan, 80);
        repository.GetApplicablePoliciesAsync(1, 2, Arg.Any<CancellationToken>()).Returns([policy]);
        var report = Report(AnalysisCategory.Coverage, Metric("coverage.line.percent", 79, AnalysisMetricDirection.HigherIsBetter));

        var evaluation = await new AnalysisPolicyEngine(repository).EvaluateAsync(report, Context(), Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Blocked, evaluation.Status);
        Assert.Equal(1, evaluation.BlockerCount);
        Assert.Contains("coverage.line.percent", evaluation.PolicySnapshotJson, StringComparison.Ordinal);
        Assert.Contains(
            AnalysisPolicySnapshot.Hash(AnalysisPolicySnapshot.Serialize(policy)),
            evaluation.PolicySnapshotJson,
            StringComparison.Ordinal);
        // Since the system default moved to 80 (A360-74), 79 % also trips the catalog policy, which
        // warns. Two violations is now the correct answer, so this picks out the PROJECT one - the
        // blocking verdict is what this test is about, and asserting a lone violation would have been
        // asserting the absence of the default rather than the presence of the block.
        var violations = AnalysisGateViolationParser.Parse(
            [new AnalysisEvaluationSnapshot(report.Id, evaluation.PolicySnapshotJson)]);
        var violation = Assert.Single(violations, item => item.Scope == AnalysisPolicyScope.Project);
        Assert.Equal("legacy.7", violation.PolicyKey);
        Assert.Equal("Block", violation.Outcome, ignoreCase: true);
        // The system default is present alongside it, and warns rather than blocking.
        var systemViolation = Assert.Single(violations, item => item.Scope == AnalysisPolicyScope.System);
        Assert.Equal("Warn", systemViolation.Outcome, ignoreCase: true);
        Assert.Equal(AnalysisPolicyScope.Project, violation.Scope);
        Assert.Equal(1, violation.Version);
        Assert.Equal("79", violation.ObservedValue);
        Assert.Equal("LessThan", violation.Operator);
        Assert.Equal("80", violation.Threshold);
        Assert.Equal(report.Id, violation.ReportId);
    }

    [Fact]
    public async Task EvaluateAsync_StrictPipelinePresetOverridesInheritedQualityRules()
    {
        const string yaml = """
            stages:
              - name: Analyze
                steps:
                  - name: Quality gate
                    type: analysis-gate
                    analysis_scope: quality
                    analysis_preset: strict
            """;
        var report = Report(
            AnalysisCategory.Duplication,
            Metric("duplication.percentage", 4, AnalysisMetricDirection.LowerIsBetter));

        var evaluation = await new AnalysisPolicyEngine(Repository()).EvaluateAsync(
            report,
            Context(yaml),
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Blocked, evaluation.Status);
        Assert.Equal(1, evaluation.BlockerCount);
        var violation = Assert.Single(AnalysisGateViolationParser.Parse(
            [new AnalysisEvaluationSnapshot(report.Id, evaluation.PolicySnapshotJson)]));
        Assert.Equal(AnalysisPolicyScope.Pipeline, violation.Scope);
        Assert.Equal("quality.duplication.percentage", violation.PolicyKey);
        Assert.Equal("3", violation.Threshold);
    }

    [Fact]
    public async Task EvaluateAsync_PipelineMinimumOverridesInheritedCoverageThreshold()
    {
        const string yaml = """
            stages:
              - name: Analyze
                steps:
                  - name: Quality gate
                    type: analysis-gate
                    analysis_scope: quality
                    analysis_rules:
                      - key: quality.coverage.line
                        minimum: 85
                        behavior: block
            """;
        var evaluation = await new AnalysisPolicyEngine(Repository()).EvaluateAsync(
            Report(
                AnalysisCategory.Coverage,
                Metric("coverage.line.percent", 82, AnalysisMetricDirection.HigherIsBetter)),
            Context(yaml),
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Blocked, evaluation.Status);
        Assert.Equal(1, evaluation.BlockerCount);
    }

    [Theory]
    [InlineData("coverage.line.percent", 74.9, AnalysisMetricDirection.HigherIsBetter, AnalysisGateStatus.Warning)]
    [InlineData("coverage.line.percent", 90, AnalysisMetricDirection.HigherIsBetter, AnalysisGateStatus.Passed)]
    [InlineData("duplication.percentage", 5.1, AnalysisMetricDirection.LowerIsBetter, AnalysisGateStatus.Warning)]
    [InlineData("complexity.cyclomatic.maximum", 14, AnalysisMetricDirection.LowerIsBetter, AnalysisGateStatus.Passed)]
    [InlineData("architecture.cycles", 1, AnalysisMetricDirection.LowerIsBetter, AnalysisGateStatus.Passed)]
    public async Task EvaluateAsync_InheritPresetPreservesObservedLegacyVerdictAndEvidence(
        string key,
        double value,
        AnalysisMetricDirection direction,
        AnalysisGateStatus expectedStatus)
    {
        const string observationYaml = """
            stages:
              - name: Analyze
                steps:
                  - name: Quality gate
                    type: analysis-gate
                    analysis_scope: quality
                    analysis_preset: inherit
            """;
        var engine = new AnalysisPolicyEngine(Repository());

        var legacy = await engine.EvaluateAsync(
            Report(AnalysisCategory.CodeQuality, Metric(key, value, direction)),
            Context(),
            Now,
            TestContext.Current.CancellationToken);
        var observed = await engine.EvaluateAsync(
            Report(AnalysisCategory.CodeQuality, Metric(key, value, direction)),
            Context(observationYaml),
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, legacy.Status);
        Assert.Equal(expectedStatus, observed.Status);
        Assert.Equal(legacy.BlockerCount, observed.BlockerCount);
        Assert.Equal(legacy.WarningCount, observed.WarningCount);
        Assert.Equal(legacy.PolicySnapshotHash, observed.PolicySnapshotHash);
        Assert.Equal(legacy.PolicySnapshotJson, observed.PolicySnapshotJson);
    }

    [Fact]
    public async Task EvaluateAsync_DistinctFindingFingerprintsProduceDistinctDecisionSnapshots()
    {
        var firstReport = Report(AnalysisCategory.Sast, Finding(AnalysisSeverity.Low, isNew: true));
        var secondFinding = Finding(AnalysisSeverity.Low, isNew: true);
        secondFinding.AnalysisFinding.Fingerprint = "another-fingerprint";
        var secondReport = Report(AnalysisCategory.Sast, secondFinding);
        var engine = new AnalysisPolicyEngine(Repository());

        var first = await engine.EvaluateAsync(
            firstReport, Context(), Now, TestContext.Current.CancellationToken);
        var second = await engine.EvaluateAsync(
            secondReport, Context(), Now, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.PolicySnapshotHash, second.PolicySnapshotHash);
        Assert.Contains("rule:fingerprint", first.PolicySnapshotJson, StringComparison.Ordinal);
        Assert.Contains("rule:another-fingerprint", second.PolicySnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_MoreThanFortySevenThousandMetrics_PreservesVerdictInBoundedDeterministicSnapshot()
    {
        const int metricCount = 50_000;
        const string metricKey = "architecture.dependency.edge";
        var metrics = Enumerable.Range(0, metricCount)
            .Select(_ => Metric(metricKey, 1, AnalysisMetricDirection.LowerIsBetter))
            .ToList();
        var firstReport = new AnalysisReport
        {
            Id = 5,
            Category = AnalysisCategory.Architecture,
            Status = AnalysisReportStatus.Passed,
            Metrics = metrics
        };
        var secondReport = new AnalysisReport
        {
            Id = 6,
            Category = AnalysisCategory.Architecture,
            Status = AnalysisReportStatus.Passed,
            Metrics = metrics.AsEnumerable().Reverse().ToList()
        };
        var repository = Repository();
        var policy = BlockingMetricPolicy(metricKey, AnalysisPolicyOperator.GreaterThan, 0);
        repository.GetApplicablePoliciesAsync(1, 2, Arg.Any<CancellationToken>()).Returns([policy]);
        var engine = new AnalysisPolicyEngine(repository);

        var first = await engine.EvaluateAsync(
            firstReport,
            Context(),
            Now,
            TestContext.Current.CancellationToken);
        var second = await engine.EvaluateAsync(
            secondReport,
            Context(),
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Blocked, first.Status);
        Assert.Equal(0, first.WarningCount);
        Assert.Equal(metricCount, first.BlockerCount);
        Assert.True(first.PolicySnapshotJson.Length < 100_000);
        Assert.Equal(first.PolicySnapshotJson, second.PolicySnapshotJson);
        Assert.Equal(first.PolicySnapshotHash, second.PolicySnapshotHash);

        using var snapshot = JsonDocument.Parse(first.PolicySnapshotJson);
        var root = snapshot.RootElement;
        Assert.False(root.GetProperty("Compacted").GetBoolean());
        Assert.Equal(0, root.GetProperty("EvaluatedFindings").GetInt32());
        Assert.Equal(metricCount, root.GetProperty("EvaluatedMetrics").GetInt32());
        Assert.Equal(0, root.GetProperty("OmittedAggregateCount").GetInt32());
        Assert.Equal(metricKey, root.GetProperty("Aggregates")[0].GetProperty("TargetKey").GetString());
        Assert.Equal(metricCount, root.GetProperty("Aggregates")[0].GetProperty("Count").GetInt32());
        Assert.Equal(
            AnalysisPolicySnapshot.Hash(AnalysisPolicySnapshot.Serialize(policy)),
            root.GetProperty("Aggregates")[0].GetProperty("PolicySnapshotHash").GetString());
        Assert.Equal(64, root.GetProperty("AggregateEvidenceHash").GetString()!.Length);
    }

    [Fact]
    public async Task EvaluateAsync_FindingExceptionScopedToOnePolicyLeavesTheOtherPolicyApplied()
    {
        var repository = Repository();
        repository.GetApplicablePoliciesAsync(1, 2, Arg.Any<CancellationToken>()).Returns(
            [FindingPolicy(7, AnalysisGateBehavior.Block), FindingPolicy(8, AnalysisGateBehavior.Warn)]);
        repository.GetActiveExceptionsAsync(2, Now, Arg.Any<CancellationToken>()).Returns(
            [new AnalysisPolicyException { Id = 40, ProjectId = 2, AnalysisPolicyId = 7, RuleId = "rule", ExpiresAt = Now.AddDays(1) }]);
        var report = Report(AnalysisCategory.Sast, Finding(AnalysisSeverity.High, isNew: false));

        var evaluation = await new AnalysisPolicyEngine(repository).EvaluateAsync(report, Context(), Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Warning, evaluation.Status);
        Assert.Equal(0, evaluation.BlockerCount);
        Assert.Equal(1, evaluation.WarningCount);
        Assert.Contains("exception:40", evaluation.PolicySnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_FindingExceptionWithoutPolicyStillCoversEveryPolicy()
    {
        var repository = Repository();
        repository.GetApplicablePoliciesAsync(1, 2, Arg.Any<CancellationToken>()).Returns(
            [FindingPolicy(7, AnalysisGateBehavior.Block), FindingPolicy(8, AnalysisGateBehavior.Warn)]);
        repository.GetActiveExceptionsAsync(2, Now, Arg.Any<CancellationToken>()).Returns(
            [new AnalysisPolicyException { Id = 41, ProjectId = 2, RuleId = "rule", ExpiresAt = Now.AddDays(1) }]);
        var report = Report(AnalysisCategory.Sast, Finding(AnalysisSeverity.High, isNew: false));

        var evaluation = await new AnalysisPolicyEngine(repository).EvaluateAsync(report, Context(), Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Passed, evaluation.Status);
    }

    [Fact]
    public async Task EvaluateAsync_MetricExceptionScopedToOnePolicyLeavesTheOtherPolicyBlocking()
    {
        var repository = Repository();
        var excepted = BlockingMetricPolicy("coverage.line.percent", AnalysisPolicyOperator.LessThan, 80);
        var other = BlockingMetricPolicy("coverage.line.percent", AnalysisPolicyOperator.LessThan, 90);
        other.Id = 8;
        repository.GetApplicablePoliciesAsync(1, 2, Arg.Any<CancellationToken>()).Returns([excepted, other]);
        repository.GetActiveExceptionsAsync(2, Now, Arg.Any<CancellationToken>()).Returns(
            [new AnalysisPolicyException { Id = 42, ProjectId = 2, AnalysisPolicyId = 7, Category = AnalysisCategory.Coverage, ExpiresAt = Now.AddDays(1) }]);
        var report = Report(AnalysisCategory.Coverage, Metric("coverage.line.percent", 79, AnalysisMetricDirection.HigherIsBetter));

        var evaluation = await new AnalysisPolicyEngine(repository).EvaluateAsync(report, Context(), Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Blocked, evaluation.Status);
        Assert.Equal(1, evaluation.BlockerCount);
        var violations = AnalysisGateViolationParser.Parse(
            [new AnalysisEvaluationSnapshot(report.Id, evaluation.PolicySnapshotJson)]);
        var blocking = Assert.Single(violations, item => item.Outcome.Equals("Block", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("legacy.8", blocking.PolicyKey);
    }

    private static AnalysisPolicy FindingPolicy(int id, AnalysisGateBehavior behavior) => new()
    {
        Id = id,
        OrganizationId = 1,
        ProjectId = 2,
        Name = $"Finding gate {id}",
        Category = AnalysisCategory.Sast,
        SeverityThreshold = AnalysisSeverity.High,
        Behavior = behavior,
        Enabled = true,
        Version = 1
    };

    [Theory]
    [InlineData(AnalysisPolicyOperator.GreaterThan, 10.0, 11.0, null, AnalysisMetricDirection.LowerIsBetter, 1)]
    [InlineData(AnalysisPolicyOperator.GreaterThan, 10.0, 10.0, null, AnalysisMetricDirection.LowerIsBetter, 0)]
    [InlineData(AnalysisPolicyOperator.GreaterThanOrEqual, 10.0, 10.0, null, AnalysisMetricDirection.LowerIsBetter, 1)]
    [InlineData(AnalysisPolicyOperator.GreaterThanOrEqual, 10.0, 9.0, null, AnalysisMetricDirection.LowerIsBetter, 0)]
    [InlineData(AnalysisPolicyOperator.LessThan, 10.0, 9.0, null, AnalysisMetricDirection.HigherIsBetter, 1)]
    [InlineData(AnalysisPolicyOperator.LessThanOrEqual, 10.0, 10.0, null, AnalysisMetricDirection.HigherIsBetter, 1)]
    [InlineData(AnalysisPolicyOperator.LessThanOrEqual, 10.0, 11.0, null, AnalysisMetricDirection.HigherIsBetter, 0)]
    [InlineData(AnalysisPolicyOperator.Equals, 10.0, 10.0000001, null, AnalysisMetricDirection.Informational, 1)]
    [InlineData(AnalysisPolicyOperator.Equals, 10.0, 10.1, null, AnalysisMetricDirection.Informational, 0)]
    [InlineData(AnalysisPolicyOperator.Changed, 2.0, 13.0, 10.0, AnalysisMetricDirection.Informational, 1)]
    [InlineData(AnalysisPolicyOperator.Changed, -2.0, 8.0, 10.0, AnalysisMetricDirection.Informational, 1)]
    [InlineData(AnalysisPolicyOperator.Changed, 2.0, 11.0, 10.0, AnalysisMetricDirection.Informational, 0)]
    [InlineData(AnalysisPolicyOperator.Changed, 2.0, 13.0, null, AnalysisMetricDirection.Informational, 0)]
    [InlineData(AnalysisPolicyOperator.Degraded, 0.0, 9.0, 10.0, AnalysisMetricDirection.HigherIsBetter, 1)]
    [InlineData(AnalysisPolicyOperator.Degraded, 0.0, 11.0, 10.0, AnalysisMetricDirection.HigherIsBetter, 0)]
    [InlineData(AnalysisPolicyOperator.Degraded, 0.0, 11.0, 10.0, AnalysisMetricDirection.LowerIsBetter, 1)]
    [InlineData(AnalysisPolicyOperator.Degraded, 0.0, 9.0, 10.0, AnalysisMetricDirection.LowerIsBetter, 0)]
    [InlineData(AnalysisPolicyOperator.Degraded, 0.0, 9.0, 10.0, AnalysisMetricDirection.Informational, 0)]
    [InlineData(AnalysisPolicyOperator.Degraded, 0.0, 9.0, null, AnalysisMetricDirection.HigherIsBetter, 0)]
    [InlineData((AnalysisPolicyOperator)99, 0.0, 9.0, 10.0, AnalysisMetricDirection.HigherIsBetter, 0)]
    [InlineData(null, 10.0, 11.0, null, AnalysisMetricDirection.LowerIsBetter, 0)]
    public async Task EvaluateAsync_ExplicitMetricPolicyBlocksOnlyWhenItsOperatorMatches(
        AnalysisPolicyOperator? policyOperator,
        double threshold,
        double value,
        double? baseline,
        AnalysisMetricDirection direction,
        int expectedBlockers)
    {
        var repository = Repository();
        var policy = BlockingMetricPolicy("custom.score", AnalysisPolicyOperator.GreaterThan, threshold);
        policy.Operator = policyOperator;
        repository.GetApplicablePoliciesAsync(1, 2, Arg.Any<CancellationToken>()).Returns([policy]);
        var metric = Metric("custom.score", value, direction);
        metric.BaselineValue = baseline;

        var evaluation = await new AnalysisPolicyEngine(repository).EvaluateAsync(
            Report(AnalysisCategory.CodeQuality, metric), Context(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(expectedBlockers, evaluation.BlockerCount);
        Assert.Equal(expectedBlockers == 1, evaluation.Status == AnalysisGateStatus.Blocked);
    }

    [Fact]
    public async Task EvaluateAsync_MetricPolicyWithoutThresholdNeverBlocks()
    {
        var repository = Repository();
        var policy = BlockingMetricPolicy("custom.score", AnalysisPolicyOperator.GreaterThan, 0);
        policy.Threshold = null;
        repository.GetApplicablePoliciesAsync(1, 2, Arg.Any<CancellationToken>()).Returns([policy]);

        var evaluation = await new AnalysisPolicyEngine(repository).EvaluateAsync(
            Report(AnalysisCategory.CodeQuality, Metric("custom.score", 50, AnalysisMetricDirection.LowerIsBetter)),
            Context(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(0, evaluation.BlockerCount);
    }

    private static Task<AnalysisEvaluation> EvaluateAsync(AnalysisCategory category, object item)
    {
        var report = Report(category, item);
        return new AnalysisPolicyEngine(Repository()).EvaluateAsync(report, Context(), Now,
            TestContext.Current.CancellationToken);
    }

    private static IAnalysisRepository Repository()
    {
        var repository = Substitute.For<IAnalysisRepository>();
        repository.GetApplicablePoliciesAsync(1, 2, Arg.Any<CancellationToken>()).Returns([]);
        repository.GetActiveExceptionsAsync(2, Now, Arg.Any<CancellationToken>()).Returns([]);
        return repository;
    }

    private static AnalysisReport Report(AnalysisCategory category, object item)
    {
        var report = new AnalysisReport { Id = 5, Category = category, Status = AnalysisReportStatus.Passed };
        if (item is AnalysisFindingOccurrence occurrence) report.Occurrences.Add(occurrence);
        if (item is AnalysisMetric metric) report.Metrics.Add(metric);
        return report;
    }

    private static AnalysisFindingOccurrence Finding(AnalysisSeverity severity, bool isNew) => new()
    {
        RuleId = "rule",
        IsNew = isNew,
        AnalysisFinding = new AnalysisFinding
        {
            Id = 3,
            Fingerprint = "fingerprint",
            Category = AnalysisCategory.Sast,
            Severity = severity
        }
    };

    private static AnalysisMetric Metric(string key, double value, AnalysisMetricDirection direction) => new()
    {
        Key = key,
        Value = value,
        Direction = direction,
        ToolName = "test"
    };

    private static AnalysisPolicy BlockingMetricPolicy(
        string metricKey,
        AnalysisPolicyOperator policyOperator,
        double threshold) => new()
        {
            Id = 7,
            OrganizationId = 1,
            ProjectId = 2,
            Name = "Hard metric gate",
            MetricKey = metricKey,
            Operator = policyOperator,
            Threshold = threshold,
            Behavior = AnalysisGateBehavior.Block,
            Enabled = true,
            Version = 1
        };

    private static AnalysisRunContext Context(string pipelineYaml = "") =>
        new(1, 2, 3, "feature", new string('a', 40), "main", PipelineYaml: pipelineYaml);
}
