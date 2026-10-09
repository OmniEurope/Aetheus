// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// PLAN-015 lot 1: what an imported quality gate must hold before the editor accepts it. A file that
/// passed here with a duplicate key, a rule with no target or a metric with no threshold would rewrite
/// the gate with rules that judge nothing.
/// </summary>
public sealed class QualityGatePolicyEditorImportTests
{
    private static UpsertAnalysisPolicyRequest Metric(string key = "quality.coverage") => new()
    {
        PolicyKey = key,
        Name = "Line coverage",
        MetricKey = "coverage.line.percent",
        Operator = AnalysisPolicyOperator.LessThan,
        Threshold = 75
    };

    private static QualityGatePolicyEditor.QualityGateExport Export(params UpsertAnalysisPolicyRequest[] rules) =>
        new() { SchemaVersion = 1, Rules = [.. rules] };

    [Fact]
    public void AValidGate_IsAccepted()
    {
        var finding = new UpsertAnalysisPolicyRequest
        {
            PolicyKey = "security.high",
            Name = "High findings",
            SeverityThreshold = AnalysisSeverity.High
        };

        Assert.True(QualityGatePolicyEditor.IsValidImport(Export(Metric(), finding)));
    }

    [Fact]
    public void NoFile_AnotherSchema_NoRule_OrTooManyRules_AreRefused()
    {
        Assert.False(QualityGatePolicyEditor.IsValidImport(null));
        Assert.False(QualityGatePolicyEditor.IsValidImport(new() { SchemaVersion = 2, Rules = [Metric()] }));
        Assert.False(QualityGatePolicyEditor.IsValidImport(Export()));
        Assert.False(QualityGatePolicyEditor.IsValidImport(
            Export([.. Enumerable.Range(0, 101).Select(i => Metric($"quality.rule-{i}"))])));
    }

    [Fact]
    public void ARuleFailingItsAnnotations_IsRefused() =>
        Assert.False(QualityGatePolicyEditor.IsValidImport(Export(Metric() with { Name = "ab" })));

    [Fact]
    public void AMissingKey_OrTheSameKeyTwiceInAnyCase_IsRefused()
    {
        Assert.False(QualityGatePolicyEditor.IsValidImport(Export(Metric() with { PolicyKey = " " })));
        Assert.False(QualityGatePolicyEditor.IsValidImport(Export(Metric("quality.a"), Metric("quality.a"))));
    }

    [Fact]
    public void ARuleWithoutTarget_IsRefused() =>
        Assert.False(QualityGatePolicyEditor.IsValidImport(Export(new UpsertAnalysisPolicyRequest
        {
            PolicyKey = "quality.nothing",
            Name = "Judges nothing"
        })));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AMetricWithoutItsOperatorOrThreshold_IsRefused(bool hasOperator, bool hasThreshold) =>
        Assert.False(QualityGatePolicyEditor.IsValidImport(Export(Metric() with
        {
            Operator = hasOperator ? AnalysisPolicyOperator.LessThan : null,
            Threshold = hasThreshold ? 75 : null
        })));

    [Theory]
    [InlineData("rule")]
    [InlineData("category")]
    [InlineData("scanner")]
    public void EachKindOfTarget_IsEnough(string target)
    {
        var rule = new UpsertAnalysisPolicyRequest
        {
            PolicyKey = "quality.target",
            Name = "One target",
            RuleId = target == "rule" ? "csharp.sql-injection" : null,
            Category = target == "category" ? AnalysisCategory.Secrets : null,
            ScannerKey = target == "scanner" ? "gitleaks" : null
        };

        Assert.True(QualityGatePolicyEditor.IsValidImport(Export(rule)));
    }
}
