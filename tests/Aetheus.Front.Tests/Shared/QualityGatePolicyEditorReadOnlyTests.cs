// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// The quality-gate editor is shown to readers as well as maintainers. A reader must see the rules
/// that will judge their pipeline, and must not be offered any control that changes them: a visible
/// but unauthorized button is a request that fails at the API with an opaque error.
/// </summary>
public sealed class QualityGatePolicyEditorReadOnlyTests : BunitContext
{
    public QualityGatePolicyEditorReadOnlyTests() => BunitTestHelper.RegisterServices(this);

    private static AnalysisPolicyDto Inherited(string key = "quality.coverage.line") => new()
    {
        Id = -11,
        PolicyKey = key,
        Name = key,
        MetricKey = "coverage.line.percent",
        Operator = AnalysisPolicyOperator.LessThan,
        Threshold = 75,
        Scope = AnalysisPolicyScope.System,
        IsInherited = true,
        IsEffective = true,
        Enabled = true,
        Version = 1
    };

    private static AnalysisPolicyDto Local(bool effective = true, string key = "project.no-cycles") => new()
    {
        Id = 20,
        ProjectId = 4,
        PolicyKey = key,
        Name = "No cycles",
        MetricKey = "architecture.cycles*",
        Operator = AnalysisPolicyOperator.GreaterThan,
        Threshold = 0,
        Scope = AnalysisPolicyScope.Project,
        IsEffective = effective,
        Enabled = effective,
        Version = 2
    };

    private IRenderedComponent<QualityGatePolicyEditor> RenderEditor(
        bool canEdit, params AnalysisPolicyDto[] policies)
    {
        var cut = Render<QualityGatePolicyEditor>(parameters => parameters
            .Add(component => component.Scope, AnalysisPolicyScope.Project)
            .Add(component => component.ProjectId, 4)
            .Add(component => component.CanEdit, canEdit)
            .Add(component => component.InitialPolicies, policies.ToList()));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        return cut;
    }

    [Fact]
    public void AReaderStillSeesTheRulesThatWillJudgeTheirPipeline()
    {
        var cut = RenderEditor(canEdit: false, Inherited(), Local());

        Assert.Contains("quality.coverage.line", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("No cycles", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AReaderIsOfferedNoEditControl()
    {
        // Every one of these ends in a 403 from the API, so showing them is a trap.
        var cut = RenderEditor(canEdit: false, Inherited(), Local());

        Assert.Empty(cut.FindAll("button[title='Edit']"));
        Assert.Null(cut.FindMenuItem("Duplicate"));
        Assert.Empty(cut.FindAll("button[title='QualityGateOverride']"));
    }

    [Fact]
    public void AMaintainerIsOfferedTheEditControls()
    {
        var cut = RenderEditor(canEdit: true, Inherited(), Local());

        Assert.NotEmpty(cut.FindAll("button[title='Edit']"));
        Assert.NotNull(cut.FindMenuItem("Duplicate"));
    }

    [Fact]
    public void AnInheritedRuleIsOfferedOverrideRatherThanDirectEdit()
    {
        // A system rule cannot be edited in place from a project; overriding creates a local copy.
        var cut = RenderEditor(canEdit: true, Inherited());

        Assert.Single(cut.FindAll("button[title='QualityGateOverride']"));
        Assert.Empty(cut.FindAll("button[title='Edit']"));
    }

    [Fact]
    public void ALocalRuleIsEditedDirectly_NotOverridden()
    {
        var cut = RenderEditor(canEdit: true, Local());

        Assert.Single(cut.FindAll("button[title='Edit']"));
        Assert.Empty(cut.FindAll("button[title='QualityGateOverride']"));
    }

    [Fact]
    public void TheInheritedAndLocalCountsAreReportedSeparately()
    {
        // The two numbers tell a maintainer how much of the gate they actually control.
        var cut = RenderEditor(canEdit: true, Inherited(), Inherited("quality.duplication"), Local());

        Assert.Contains("QualityGateInheritedCount", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("QualityGateLocalCount", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(">2<", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(">1<", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisabledRuleIsLabelledDisabled_SoItIsNotMistakenForEnforced()
    {
        var cut = RenderEditor(canEdit: true, Local(effective: false));

        Assert.Contains("Disabled", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnabledRuleIsLabelledEnabled()
    {
        var cut = RenderEditor(canEdit: true, Local());

        Assert.Contains("Enabled", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoPoliciesAtAll_TheEmptyStateIsShownRatherThanABlankTable()
    {
        var cut = RenderEditor(canEdit: true);

        Assert.Contains("NoRecords", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheYamlExampleIsOfferedEvenToAReader()
    {
        // It is documentation, not a mutation, and it is how a reader learns the file format.
        var cut = RenderEditor(canEdit: false, Inherited());

        Assert.Contains("analysis_preset: recommended", cut.Markup, StringComparison.Ordinal);
        // OE's code block, with its copy button in the header.
        Assert.NotNull(cut.Find(".omni-code-block.quality-gate-yaml-panel figcaption button"));
    }
}
