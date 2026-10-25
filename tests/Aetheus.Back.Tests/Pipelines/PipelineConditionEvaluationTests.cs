// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// A stage or step condition is exactly one supported form on its whole text. The former patterns
/// matched anywhere in the text: <c>or(eq(A), eq(B))</c> was read as its first <c>eq</c> alone, and
/// <c>and(...)</c>, <c>not(...)</c>, an unknown function or trailing text were accepted the same way,
/// silently, on conditions that gate approvals, rollbacks and production deployments.
/// </summary>
public sealed class PipelineConditionEvaluationTests
{
    private static PipelineConditionVerdict Evaluate(
        string? condition, Dictionary<string, string>? variables = null, bool previousStageFailed = false)
        => PipelineRunHelpers.EvaluateCondition(condition, variables ?? [], previousStageFailed);

    // --- The half-evaluated forms: each one was read as its first eq; now invalid, never met. ---

    [Theory]
    // Only the second operand holds: read as its first eq, the or() was not met.
    [InlineData("or(eq(variables['A'], 'x'), eq(variables['B'], 'y'))", "no", "y")]
    // The second operand fails: read as its first eq, the and() was met.
    [InlineData("and(eq(variables['A'], 'x'), eq(variables['B'], 'y'))", "x", "no")]
    // The negation was dropped: read as its operand, the not() was met.
    [InlineData("not(eq(variables['A'], 'x'))", "x", "y")]
    // An unknown function around a valid comparison was read as the comparison.
    [InlineData("foo(eq(variables['A'], 'x'))", "x", "y")]
    // Text after a valid comparison was ignored.
    [InlineData("eq(variables['A'], 'x') and more", "x", "y")]
    [InlineData("eq(variables['A'], 'x'), eq(variables['B'], 'y')", "x", "y")]
    [InlineData("prefix eq(variables['A'], 'x')", "x", "y")]
    [InlineData("always() || failed()", "x", "y")]
    [InlineData("succeeded(variables['A'])", "x", "y")]
    public void UnsupportedForm_IsInvalid_AndNeverMet(string condition, string a, string b)
    {
        var verdict = Evaluate(condition, new() { ["A"] = a, ["B"] = b }, previousStageFailed: true);

        Assert.True(verdict.IsInvalid);
        Assert.False(verdict.Met);
        Assert.Contains("is not supported", verdict.Error, StringComparison.Ordinal);
        Assert.Equal(verdict.Error, PipelineConditionEvaluator.FindSyntaxError(condition));
    }

    // --- Every form the repository's definitions use evaluates exactly as before. ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoCondition_IsMet(string? condition)
    {
        Assert.Equal(PipelineConditionVerdict.Of(true), Evaluate(condition, previousStageFailed: true));
        Assert.Null(PipelineConditionEvaluator.FindSyntaxError(condition));
    }

    [Theory]
    [InlineData("always()", false, true)]
    [InlineData("always()", true, true)]
    [InlineData(" ALWAYS() ", true, true)]
    [InlineData("succeeded()", false, true)]
    [InlineData("succeeded()", true, false)]
    [InlineData("failed()", true, true)]
    [InlineData("failed()", false, false)]
    [InlineData("cancelled()", false, false)]
    [InlineData("cancelled()", true, false)]
    public void StatusFunction_KeepsItsSemantics(string condition, bool previousStageFailed, bool expected)
        => Assert.Equal(PipelineConditionVerdict.Of(expected), Evaluate(condition, previousStageFailed: previousStageFailed));

    [Theory]
    // eq: equal (case-insensitive) is met; different or absent is not.
    [InlineData("eq(variables['QA_BOOTSTRAP_MODE'], '0')", "QA_BOOTSTRAP_MODE", "0", true)]
    [InlineData("eq(variables['DEPLOY_STRATEGY'], 'bluegreen')", "DEPLOY_STRATEGY", "BlueGreen", true)]
    [InlineData("eq(variables['DEPLOY_STRATEGY'], 'bluegreen')", "DEPLOY_STRATEGY", "rolling", false)]
    [InlineData("eq(variables['APPLICATION_PAYLOAD_SEALED'], 'false')", "APPLICATION_PAYLOAD_SEALED", "false", true)]
    [InlineData("eq(variables['HAS_PYTHON'], 'true')", null, null, false)]
    // ne: different is met, equal is not, ABSENT IS MET (a generic approval relies on it).
    [InlineData("ne(variables['NIGHTLY_EVIDENCE'], 'reuse')", "NIGHTLY_EVIDENCE", "fresh", true)]
    [InlineData("ne(variables['NIGHTLY_EVIDENCE'], 'reuse')", "NIGHTLY_EVIDENCE", "REUSE", false)]
    [InlineData("ne(variables['AETHEUS_CI_CONTINUE_ON_FINDINGS'], 'true')", null, null, true)]
    [InlineData("ne(variables['DELIVERY_BASELINE_BOOTSTRAP'], 'false')", null, null, true)]
    // contains: substring (case-insensitive) is met; absent is not.
    [InlineData("contains(variables['BRANCH'], 'release/')", "BRANCH", "Release/1.9", true)]
    [InlineData("contains(variables['BRANCH'], 'release/')", "BRANCH", "develop", false)]
    [InlineData("contains(variables['BRANCH'], 'release/')", null, null, false)]
    // Spacing and case the former patterns accepted are still accepted.
    [InlineData("  EQ ( variables['MODE'] ,'x' )  ", "MODE", "x", true)]
    [InlineData("eq(variables['MODE'], '')", "MODE", "", true)]
    public void VariableComparison_KeepsItsSemantics(string condition, string? name, string? value, bool expected)
    {
        var variables = name is null ? new Dictionary<string, string>() : new() { [name] = value! };

        Assert.Equal(PipelineConditionVerdict.Of(expected), Evaluate(condition, variables));
        Assert.Null(PipelineConditionEvaluator.FindSyntaxError(condition));
    }

    // --- Definition validation: save, copy and launch read the same problems. ---

    [Fact]
    public void Definition_UsingOnlySupportedForms_HasNoProblem()
    {
        var definition = Parse("""
            name: supported
            trigger: manual
            stages:
              - name: Build
                condition: "eq(variables['HAS_DOTNET'], 'true')"
                steps:
                  - name: build
                    condition: always()
                    shell: dotnet build
              - name: Rollback
                condition: failed()
                jobs:
                  - name: undo
                    condition: "ne(variables['DEPLOY_HAS_API'], 'true')"
                    agent: linux
                    steps:
                      - name: restore
                        condition: "succeeded()"
                        shell: sh rollback.sh
            """);

        Assert.Empty(PipelineConditionEvaluator.FindDefinitionProblems(definition));
        Assert.Empty(ValidateStages(definition));
    }

    [Fact]
    public void Definition_WithUnsupportedConditions_NamesEachOwner()
    {
        var definition = Parse("""
            name: unsupported
            trigger: manual
            stages:
              - name: Approve
                condition: "or(eq(variables['A'], 'x'), eq(variables['B'], 'y'))"
                steps:
                  - name: wait
                    condition: "eq(variables['A'], 'x') trailing"
                    shell: echo
              - name: Deploy
                jobs:
                  - name: ship
                    condition: "not(eq(variables['A'], 'x'))"
                    agent: linux
                    steps:
                      - name: up
                        condition: "foo()"
                        shell: echo
            """);

        var problems = PipelineConditionEvaluator.FindDefinitionProblems(definition);

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("Stage 'Approve': condition 'or(", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Job 'ship' in stage 'Deploy': condition 'not(", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Step 'wait' in stage 'Approve': condition 'eq(", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Step 'up' in stage 'ship': condition 'foo()'", StringComparison.Ordinal));
        Assert.All(problems, p => Assert.Contains(PipelineConditionEvaluator.SupportedForms, p, StringComparison.Ordinal));
        // The editor's save validation refuses the same definition.
        Assert.Equal(problems, ValidateStages(definition));
    }

    private static PipelineYamlDefinition Parse(string yaml)
        => YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml);

    private static List<string> ValidateStages(PipelineYamlDefinition definition)
    {
        var errors = new List<string>();
        PipelineDefinitionValidator.ValidateStages(definition, errors, []);
        return errors;
    }
}
