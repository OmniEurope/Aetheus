// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Shared;

public sealed class QualityGatePolicyEditorTests : BunitContext
{
    public QualityGatePolicyEditorTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void ProjectView_DistinguishesInheritedAndEditableRules()
    {
        var policies = new List<AnalysisPolicyDto>
        {
            new()
            {
                Id = -11,
                PolicyKey = "quality.coverage.line",
                Name = "quality.coverage.line",
                MetricKey = "coverage.line.percent",
                Operator = AnalysisPolicyOperator.LessThan,
                Threshold = 75,
                Scope = AnalysisPolicyScope.System,
                IsInherited = true,
                IsEffective = true,
                Enabled = true,
                Version = 1
            },
            new()
            {
                Id = 20,
                ProjectId = 4,
                PolicyKey = "project.no-cycles",
                Name = "No cycles",
                MetricKey = "architecture.cycles*",
                Operator = AnalysisPolicyOperator.GreaterThan,
                Threshold = 0,
                Scope = AnalysisPolicyScope.Project,
                IsEffective = true,
                Enabled = true,
                Version = 2
            }
        };

        var cut = Render<QualityGatePolicyEditor>(parameters => parameters
            .Add(component => component.Scope, AnalysisPolicyScope.Project)
            .Add(component => component.ProjectId, 4)
            .Add(component => component.CanEdit, true)
            .Add(component => component.InitialPolicies, policies));

        cut.WaitForState(() => cut.Markup.Contains("quality.coverage.line"));
        Assert.Contains("QualityGateScopeSystem", cut.Markup);
        Assert.Contains("QualityGateScopeProject", cut.Markup);
        Assert.Single(cut.FindAll("button[title='QualityGateOverride']"));
        Assert.Single(cut.FindAll("button[title='Edit']"));
        Assert.Contains("QualityGateOptionalTitle", cut.Markup);
        Assert.NotNull(cut.Find(".omni-code-block.quality-gate-yaml-panel figcaption button")); // The YAML example and its copy button
        Assert.Contains("analysis_preset: recommended", cut.Markup);

        // STD-BTN detection 11: history, duplicate, presets, import and export sit in "More actions" menus.
        var entries = MenuEntries(cut);
        Assert.Single(entries, entry => entry == "QualityGateHistory");
        Assert.Equal(2, entries.Count(entry => entry == "Duplicate"));
        Assert.Contains("Recommended", entries);
        Assert.Contains("QualityGateStrict", entries);
        Assert.Contains("Import", entries);
        Assert.Contains("Export", entries);
    }

    /// <summary>Opens every overflow menu in turn and returns the labels of their entries.</summary>
    private static List<string> MenuEntries(IRenderedComponent<QualityGatePolicyEditor> cut)
    {
        var labels = new List<string>();
        var count = cut.FindAll(".omni-overflow-menu__trigger").Count;
        for (var index = 0; index < count; index++)
        {
            cut.FindAll(".omni-overflow-menu__trigger")[index].Click();
            labels.AddRange(cut.FindAll(".omni-menu__item .omni-menu__label").Select(label => label.TextContent.Trim()));
            cut.FindAll(".omni-overflow-menu__trigger")[index].Click();
        }

        return labels;
    }

    [Fact]
    public void RuleList_ShowsEachRuleCriterion_AsItsMetricThresholdOrItsFindingFilters()
    {
        var policies = new List<AnalysisPolicyDto>
        {
            new()
            {
                Id = 31, PolicyKey = "metric.rule", Name = "Coverage floor", MetricKey = "coverage.line.percent",
                Operator = AnalysisPolicyOperator.LessThan, Threshold = 80, Scope = AnalysisPolicyScope.Global,
                IsEffective = true, Enabled = true, Version = 1
            },
            new()
            {
                Id = 32, PolicyKey = "finding.rule", Name = "New high semgrep", Category = AnalysisCategory.Sast,
                ScannerKey = "semgrep", RuleId = "csharp.sql-injection", SeverityThreshold = AnalysisSeverity.High,
                NewFindingsOnly = true, Scope = AnalysisPolicyScope.Global, IsEffective = true, Enabled = true, Version = 1
            },
            new()
            {
                Id = 33, PolicyKey = "any.rule", Name = "Any secret", Category = AnalysisCategory.Secrets,
                Scope = AnalysisPolicyScope.Global, IsEffective = true, Enabled = true, Version = 1
            }
        };

        var cut = Render<QualityGatePolicyEditor>(parameters => parameters
            .Add(component => component.Scope, AnalysisPolicyScope.Global)
            .Add(component => component.CanEdit, true)
            .Add(component => component.InitialPolicies, policies));

        cut.WaitForState(() => cut.Markup.Contains("Coverage floor"));
        Assert.Contains("coverage.line.percent LessThan 80", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(
            "Sast / semgrep / csharp.sql-injection / &gt;= High / QualityGateNewOnlyShort", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(">Secrets<", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void RuleForm_ExposesAccessibleNamesForEveryInput()
    {
        var cut = Render<QualityGatePolicyEditor>(parameters => parameters
            .Add(component => component.Scope, AnalysisPolicyScope.Global)
            .Add(component => component.CanEdit, true)
            .Add(component => component.InitialPolicies, []));

        cut.FindAll("button")
            .Single(button => button.Names().Contains("QualityGateAddRule", StringComparison.Ordinal))
            .Click();

        var expectedLabels = new[]
        {
            "QualityGateRuleType",
            "Name",
            "QualityGatePolicyKey",
            "Category",
            "AnalysisScanner",
            "AnalysisRule",
            "Severity",
            "Branch",
            "Environment",
            "Behavior",
            "Priority"
        };

        var labelsWithInvalidCount = expectedLabels
            .Where(label => cut.FindAll($"[aria-label='{label}']").Count != 1)
            .ToArray();
        Assert.Empty(labelsWithInvalidCount);

        Assert.Single(cut.FindAll("input[aria-label='Priority']"));
    }
}
