// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

internal static class PipelineAnalysisGatePolicyResolver
{
    public static List<ResolvedAnalysisPolicy> Apply(
        IReadOnlyCollection<ResolvedAnalysisPolicy> inherited,
        string pipelineYaml,
        AnalysisCategory reportCategory,
        DateTime now)
    {
        var definition = YamlParsingHelper.ParseAndValidate(pipelineYaml);
        if (definition is null) return [.. inherited];
        var scope = AnalysisGateScopes.ForCategory(reportCategory);
        var gate = YamlParsingHelper.FlattenJobs(definition)
            .SelectMany(stage => stage.Steps)
            .SingleOrDefault(step =>
                string.Equals(step.Type, "analysis-gate", StringComparison.OrdinalIgnoreCase)
                && AnalysisGateScopes.Normalize(step.AnalysisScope) == scope);
        if (gate is null) return [.. inherited];

        var rules = ExpandPreset(gate.AnalysisPreset)
            .Concat(gate.AnalysisRules)
            .GroupBy(rule => rule.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        if (rules.Count == 0) return [.. inherited];

        var resolved = inherited.ToDictionary(
            item => AnalysisPolicyKey.ForExisting(item.Policy),
            StringComparer.OrdinalIgnoreCase);
        var id = int.MinValue;
        foreach (var rule in rules)
        {
            resolved.TryGetValue(rule.Key, out var parent);
            var policy = ApplyRule(parent?.Policy, rule, id++, now);
            resolved[rule.Key] = new ResolvedAnalysisPolicy(
                policy,
                AnalysisPolicyScope.Pipeline,
                false,
                parent is not null,
                parent?.Scope);
        }
        return resolved.Values
            .OrderByDescending(item => item.Policy.Priority)
            .ThenBy(item => AnalysisPolicyKey.ForExisting(item.Policy), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AnalysisPolicy ApplyRule(
        AnalysisPolicy? parent,
        PipelineAnalysisGateRuleDefinition rule,
        int id,
        DateTime now)
    {
        PipelineAnalysisGateRuleValidator.TryParseEnum<AnalysisCategory>(rule.Category, out var category);
        PipelineAnalysisGateRuleValidator.TryParseEnum<AnalysisSeverity>(rule.Severity, out var severity);
        PipelineAnalysisGateRuleValidator.TryParseEnum<AnalysisGateBehavior>(rule.Behavior, out var behavior);
        PipelineAnalysisGateRuleValidator.TryParseEnum<AnalysisPolicyOperator>(rule.Operator, out var policyOperator);
        var policy = parent is null ? new AnalysisPolicy() : Clone(parent);
        policy.Id = id;
        policy.OrganizationId = null;
        policy.ProjectId = null;
        policy.PolicyKey = rule.Key;
        policy.Name = parent?.Name ?? rule.Key;
        policy.Category = category ?? policy.Category;
        policy.ScannerKey = rule.Scanner ?? policy.ScannerKey;
        policy.RuleId = rule.Rule ?? policy.RuleId;
        policy.SeverityThreshold = severity ?? policy.SeverityThreshold;
        policy.NewFindingsOnly = rule.NewFindingsOnly ?? policy.NewFindingsOnly;
        policy.MetricKey = rule.Metric ?? policy.MetricKey;
        ApplyThreshold(policy, rule, policyOperator);
        ApplyPolicyMetadata(policy, parent, rule, behavior, now);
        return policy;
    }

    private static void ApplyThreshold(
        AnalysisPolicy policy,
        PipelineAnalysisGateRuleDefinition rule,
        AnalysisPolicyOperator? policyOperator)
    {
        if (rule.Minimum.HasValue)
        {
            policy.Operator = AnalysisPolicyOperator.LessThan;
            policy.Threshold = rule.Minimum;
        }
        else if (rule.Maximum.HasValue)
        {
            policy.Operator = AnalysisPolicyOperator.GreaterThan;
            policy.Threshold = rule.Maximum;
        }
        else if (policyOperator.HasValue)
        {
            policy.Operator = policyOperator;
            policy.Threshold = rule.Threshold;
        }
    }

    private static void ApplyPolicyMetadata(
        AnalysisPolicy policy,
        AnalysisPolicy? parent,
        PipelineAnalysisGateRuleDefinition rule,
        AnalysisGateBehavior? behavior,
        DateTime now)
    {
        policy.BranchPattern = rule.Branch ?? policy.BranchPattern;
        policy.EnvironmentPattern = rule.Environment ?? policy.EnvironmentPattern;
        policy.Behavior = behavior ?? parent?.Behavior ?? AnalysisGateBehavior.Block;
        policy.Priority = rule.Priority ?? parent?.Priority ?? 100;
        policy.Enabled = rule.Enabled ?? parent?.Enabled ?? true;
        policy.Version = 1;
        policy.CreatedAt = now;
        policy.UpdatedAt = now;
    }

    private static AnalysisPolicy Clone(AnalysisPolicy source) => new()
    {
        PolicyKey = source.PolicyKey,
        Name = source.Name,
        Category = source.Category,
        ScannerKey = source.ScannerKey,
        RuleId = source.RuleId,
        SeverityThreshold = source.SeverityThreshold,
        NewFindingsOnly = source.NewFindingsOnly,
        MetricKey = source.MetricKey,
        Operator = source.Operator,
        Threshold = source.Threshold,
        BranchPattern = source.BranchPattern,
        EnvironmentPattern = source.EnvironmentPattern,
        Behavior = source.Behavior,
        Priority = source.Priority,
        Enabled = source.Enabled
    };

    private static IEnumerable<PipelineAnalysisGateRuleDefinition> ExpandPreset(string? preset) =>
        preset?.Trim().ToLowerInvariant() switch
        {
            "recommended" =>
            [
                Minimum("quality.coverage.line", 75, "block"),
                Maximum("quality.duplication.percentage", 5, "warn"),
                Maximum("quality.complexity.maximum", 25, "warn")
            ],
            "strict" =>
            [
                Minimum("quality.coverage.line", 85, "block"),
                Maximum("quality.duplication.percentage", 3, "block"),
                Maximum("quality.complexity.maximum", 15, "block")
            ],
            _ => []
        };

    private static PipelineAnalysisGateRuleDefinition Minimum(
        string key, double value, string behavior) =>
        new() { Key = key, Minimum = value, Behavior = behavior };

    private static PipelineAnalysisGateRuleDefinition Maximum(
        string key, double value, string behavior) =>
        new() { Key = key, Maximum = value, Behavior = behavior };
}
