// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Shared;

public static class PipelineAnalysisGradingValidator
{
    public static List<string> Validate(
        string? scope,
        PipelineAnalysisGradingDefinition? grading)
    {
        if (grading is null) return [];
        var errors = new List<string>();
        ValidateHeader(grading, errors);
        var requiredDomains = ValidateRequiredDomains(grading.RequiredDomains, errors);
        var configuredDomains = ValidateRules(scope, grading.Rules, errors);
        foreach (var domain in requiredDomains.Except(configuredDomains))
            errors.Add($"analysis_grading required domain '{DomainToken(domain)}' has no grading rule.");
        return errors;
    }

    private static void ValidateHeader(
        PipelineAnalysisGradingDefinition grading,
        ICollection<string> errors)
    {
        if (grading.Version != 1)
            errors.Add("analysis_grading version must be 1.");
        if (!TryParseGrade(grading.MinimumGrade, out _))
            errors.Add("analysis_grading minimum_grade must be A, B, C, D, E or F.");
        if (grading.RequiredDomains.Count > 5)
            errors.Add("analysis_grading cannot contain more than 5 required domains.");
        if (grading.Rules.Count is 0 or > 100)
            errors.Add("analysis_grading rules must contain between 1 and 100 rules.");
    }

    private static HashSet<AnalysisGradeDomain> ValidateRequiredDomains(
        IEnumerable<string> domains,
        ICollection<string> errors)
    {
        var requiredDomains = new HashSet<AnalysisGradeDomain>();
        foreach (var domain in domains)
        {
            if (!TryParseDomain(domain, out var parsed))
                errors.Add($"analysis_grading required domain '{domain}' is invalid.");
            else if (!requiredDomains.Add(parsed!.Value))
                errors.Add($"analysis_grading required domain '{domain}' is declared more than once.");
        }
        return requiredDomains;
    }

    private static HashSet<AnalysisGradeDomain> ValidateRules(
        string? scope,
        IEnumerable<PipelineAnalysisGradeRuleDefinition> rules,
        ICollection<string> errors)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredDomains = new HashSet<AnalysisGradeDomain>();
        foreach (var rule in rules.Take(100))
        {
            ValidateRule(scope, rule, keys, configuredDomains, errors);
        }
        return configuredDomains;
    }

    private static void ValidateRule(
        string? scope,
        PipelineAnalysisGradeRuleDefinition rule,
        ISet<string> keys,
        ISet<AnalysisGradeDomain> configuredDomains,
        ICollection<string> errors)
    {
        var label = string.IsNullOrWhiteSpace(rule.Key) ? "<missing>" : rule.Key;
        if (!PipelineAnalysisValidation.IsValidKey(rule.Key))
            errors.Add($"analysis grading rule '{label}' key must use lowercase letters, digits, dots or hyphens.");
        else if (!keys.Add(rule.Key))
            errors.Add($"analysis grading rule '{label}' is declared more than once.");
        if (!TryParseDomain(rule.Domain, out var domain))
            errors.Add($"analysis grading rule '{label}' domain is invalid.");
        else configuredDomains.Add(domain!.Value);
        if (!TryParseDirection(rule.Direction, out _))
            errors.Add($"analysis grading rule '{label}' direction must be higher-is-better or lower-is-better.");
        ValidateRuleCategory(scope, rule, label, errors);
        ValidateTarget(rule, label, errors);
        if (TooLong(rule.Metric, 300)) errors.Add($"analysis grading rule '{label}' metric is too long.");
        ValidateBands(rule, label, errors);
    }

    // A rule grades exactly one thing: a metric, one severity's finding count, or the weighted score of
    // the open findings (recette R2-119), which only a lower-is-better direction can grade.
    private static void ValidateTarget(
        PipelineAnalysisGradeRuleDefinition rule,
        string label,
        ICollection<string> errors)
    {
        if (!PipelineAnalysisGateRuleValidator.TryParseEnum<AnalysisSeverity>(rule.Severity, out _))
            errors.Add($"analysis grading rule '{label}' severity is invalid.");
        var targets = new[] { rule.Metric, rule.Severity, rule.FindingScore }
            .Count(value => !string.IsNullOrWhiteSpace(value));
        if (targets != 1)
            errors.Add($"analysis grading rule '{label}' must target exactly one metric, finding severity or finding score.");
        if (string.IsNullOrWhiteSpace(rule.FindingScore)) return;
        if (!IsFindingScore(rule))
            errors.Add($"analysis grading rule '{label}' finding_score must be '{AnalysisFindingScore.Counted}' or '{AnalysisFindingScore.Weighted}'.");
        if (TryParseDirection(rule.Direction, out var direction) && direction != AnalysisMetricDirection.LowerIsBetter)
            errors.Add($"analysis grading rule '{label}' finding score must be graded lower-is-better.");
    }

    public static bool IsWeightedFindingScore(PipelineAnalysisGradeRuleDefinition rule) =>
        string.Equals(rule.FindingScore?.Trim(), AnalysisFindingScore.Weighted, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a rule grading its open findings, counted (<c>count</c>) or weighted.</summary>
    public static bool IsFindingScore(PipelineAnalysisGradeRuleDefinition rule) =>
        IsWeightedFindingScore(rule)
        || string.Equals(rule.FindingScore?.Trim(), AnalysisFindingScore.Counted, StringComparison.OrdinalIgnoreCase);

    private static void ValidateRuleCategory(
        string? scope,
        PipelineAnalysisGradeRuleDefinition rule,
        string label,
        ICollection<string> errors)
    {
        if (!PipelineAnalysisGateRuleValidator.TryParseEnum<AnalysisCategory>(rule.Category, out var category))
            errors.Add($"analysis grading rule '{label}' category is invalid.");
        else if (category.HasValue
                 && AnalysisGateScopes.ForCategory(category.Value) != AnalysisGateScopes.Normalize(scope))
            errors.Add($"analysis grading rule '{label}' category does not match the gate scope.");
    }

    public static bool TryParseGrade(string? value, out AnalysisGrade? grade) =>
        PipelineAnalysisGateRuleValidator.TryParseEnum(value, out grade);

    public static bool TryParseDomain(string? value, out AnalysisGradeDomain? domain) =>
        PipelineAnalysisGateRuleValidator.TryParseEnum(value, out domain);

    public static bool TryParseDirection(string? value, out AnalysisMetricDirection? direction)
    {
        direction = Normalize(value) switch
        {
            "higherisbetter" => AnalysisMetricDirection.HigherIsBetter,
            "lowerisbetter" => AnalysisMetricDirection.LowerIsBetter,
            _ => null
        };
        return direction.HasValue;
    }

    public static string DomainToken(AnalysisGradeDomain domain) => domain switch
    {
        AnalysisGradeDomain.CodeQuality => "code-quality",
        _ => domain.ToString().ToLowerInvariant()
    };

    private static void ValidateBands(
        PipelineAnalysisGradeRuleDefinition rule,
        string label,
        ICollection<string> errors)
    {
        // A finding score, counted or weighted, may leave E open: above D is E, and only a critical
        // finding gives F (recette R2-119). Every other rule needs its five thresholds.
        var findingScore = IsFindingScore(rule);
        var values = findingScore && !rule.E.HasValue
            ? new[] { rule.A, rule.B, rule.C, rule.D }
            : new[] { rule.A, rule.B, rule.C, rule.D, rule.E };
        if (values.Any(value => !value.HasValue || !double.IsFinite(value.Value)))
        {
            errors.Add(findingScore
                ? $"analysis grading rule '{label}' must define finite A, B, C and D thresholds (E is optional)."
                : $"analysis grading rule '{label}' must define finite A, B, C, D and E thresholds.");
            return;
        }
        if (!TryParseDirection(rule.Direction, out var direction)) return;
        var ordered = values.Select(value => value!.Value).ToArray();
        var valid = direction == AnalysisMetricDirection.HigherIsBetter
            ? ordered.Zip(ordered.Skip(1), (left, right) => left >= right).All(result => result)
            : ordered.Zip(ordered.Skip(1), (left, right) => left <= right).All(result => result);
        if (!valid)
            errors.Add($"analysis grading rule '{label}' thresholds are not monotonic for its direction.");
    }

    private static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty)
            .Where(character => character is not ('-' or '_' or ' ')))
            .ToLowerInvariant();

    private static bool TooLong(string? value, int max) => value?.Length > max;
}
