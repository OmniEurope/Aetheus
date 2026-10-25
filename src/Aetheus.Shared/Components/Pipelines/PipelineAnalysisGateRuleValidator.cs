// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

public static class PipelineAnalysisGateRuleValidator
{
    public static List<string> Validate(
        string? scope,
        string? preset,
        IReadOnlyList<PipelineAnalysisGateRuleDefinition> rules)
    {
        var errors = new List<string>();
        var normalizedScope = AnalysisGateScopes.Normalize(scope);
        var normalizedPreset = NormalizeToken(preset);
        if (normalizedPreset is not ("" or "inherit" or "recommended" or "strict"))
            errors.Add("analysis_preset must be inherit, recommended or strict.");
        if (normalizedScope == AnalysisGateScopes.Security && normalizedPreset is "recommended" or "strict")
            errors.Add("analysis_preset recommended and strict are available only for quality gates.");
        if (rules.Count > 100)
            errors.Add("analysis_rules cannot contain more than 100 rules.");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules.Take(100))
        {
            var label = string.IsNullOrWhiteSpace(rule.Key) ? "<missing>" : rule.Key;
            ValidateIdentity(errors, keys, rule, label);
            ValidateThreshold(errors, rule, label);
            ValidateEnums(errors, rule, label, normalizedScope);
            ValidateBounds(errors, rule, label);
        }
        return errors;
    }

    private static void ValidateIdentity(
        ICollection<string> errors,
        ISet<string> keys,
        PipelineAnalysisGateRuleDefinition rule,
        string label)
    {
        if (!PipelineAnalysisValidation.IsValidKey(rule.Key))
            errors.Add($"analysis rule '{label}' key must use lowercase letters, digits, dots or hyphens.");
        else if (!keys.Add(rule.Key))
            errors.Add($"analysis rule '{label}' is declared more than once.");
    }

    private static void ValidateThreshold(
        ICollection<string> errors,
        PipelineAnalysisGateRuleDefinition rule,
        string label)
    {
        if (rule.Minimum.HasValue && rule.Maximum.HasValue)
            errors.Add($"analysis rule '{label}' cannot set both minimum and maximum.");
        if ((rule.Minimum.HasValue || rule.Maximum.HasValue)
            && (!string.IsNullOrWhiteSpace(rule.Operator) || rule.Threshold.HasValue))
            errors.Add($"analysis rule '{label}' must use minimum/maximum or operator/threshold, not both.");
        if (string.IsNullOrWhiteSpace(rule.Operator) != !rule.Threshold.HasValue)
            errors.Add($"analysis rule '{label}' operator and threshold must be provided together.");
        if (!IsFinite(rule.Minimum) || !IsFinite(rule.Maximum) || !IsFinite(rule.Threshold))
            errors.Add($"analysis rule '{label}' thresholds must be finite numbers.");
    }

    private static void ValidateEnums(
        ICollection<string> errors,
        PipelineAnalysisGateRuleDefinition rule,
        string label,
        string normalizedScope)
    {
        if (!TryParseEnum<AnalysisPolicyOperator>(rule.Operator, out _))
            errors.Add($"analysis rule '{label}' operator is invalid.");
        if (!TryParseEnum<AnalysisGateBehavior>(rule.Behavior, out _))
            errors.Add($"analysis rule '{label}' behavior must be warn or block.");
        if (!TryParseEnum<AnalysisSeverity>(rule.Severity, out _))
            errors.Add($"analysis rule '{label}' severity is invalid.");
        if (!TryParseEnum<AnalysisCategory>(rule.Category, out var category))
            errors.Add($"analysis rule '{label}' category is invalid.");
        else if (category.HasValue
                 && AnalysisGateScopes.ForCategory(category.Value) != normalizedScope)
            errors.Add($"analysis rule '{label}' category does not match the {normalizedScope} gate.");
    }

    private static void ValidateBounds(
        ICollection<string> errors,
        PipelineAnalysisGateRuleDefinition rule,
        string label)
    {
        if (rule.Priority is < -10_000 or > 10_000)
            errors.Add($"analysis rule '{label}' priority must be between -10000 and 10000.");
        if (TooLong(rule.Metric, 300) || TooLong(rule.Rule, 300)
            || TooLong(rule.Branch, 300) || TooLong(rule.Environment, 300)
            || TooLong(rule.Scanner, 100))
            errors.Add($"analysis rule '{label}' contains a value that is too long.");
    }

    public static bool TryParseEnum<TEnum>(string? value, out TEnum? parsed)
        where TEnum : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var normalized = NormalizeToken(value);
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (NormalizeToken(candidate.ToString()) != normalized) continue;
            parsed = candidate;
            return true;
        }
        return false;
    }

    private static string NormalizeToken(string? value) =>
        string.Concat((value ?? string.Empty)
            .Where(character => character is not ('-' or '_' or ' ')))
            .ToLowerInvariant();

    private static bool IsFinite(double? value) =>
        !value.HasValue || double.IsFinite(value.Value);

    private static bool TooLong(string? value, int max) =>
        value?.Length > max;
}
