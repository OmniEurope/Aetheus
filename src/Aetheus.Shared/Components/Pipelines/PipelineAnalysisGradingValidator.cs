// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

public static class PipelineAnalysisGradingValidator
{
    public static List<string> Validate(
        string? scope,
        PipelineAnalysisGradingDefinition? grading)
    {
        if (grading is null) return [];
        var (header, rules, missingDomains) = Collect(scope, grading);
        return [.. header, .. rules.SelectMany(errors => errors), .. missingDomains];
    }

    /// <summary>The same errors as <see cref="Validate"/>, split between the block itself (version, minimum grade,
    /// rule count, required domains) and each rule, in the order of <see cref="PipelineAnalysisGradingDefinition.Rules"/>
    /// (the first 100), so the grade engine can grade the valid rules and turn each rejected one into an F.</summary>
    public static PipelineAnalysisGradingDiagnosis Diagnose(
        string? scope,
        PipelineAnalysisGradingDefinition grading)
    {
        ArgumentNullException.ThrowIfNull(grading);
        var (header, rules, missingDomains) = Collect(scope, grading);
        return new PipelineAnalysisGradingDiagnosis([.. header, .. missingDomains], rules);
    }

    private static (List<string> Header, List<List<string>> Rules, List<string> MissingDomains) Collect(
        string? scope,
        PipelineAnalysisGradingDefinition grading)
    {
        var header = new List<string>();
        ValidateHeader(grading, header);
        var requiredDomains = ValidateRequiredDomains(grading.RequiredDomains, header);
        var (rules, configuredDomains) = ValidateRules(scope, grading.Rules);
        var missingDomains = requiredDomains.Except(configuredDomains)
            .Select(domain => $"analysis_grading required domain '{DomainToken(domain)}' has no grading rule.")
            .ToList();
        return (header, rules, missingDomains);
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

    private static (List<List<string>> Errors, HashSet<AnalysisGradeDomain> ConfiguredDomains) ValidateRules(
        string? scope,
        IEnumerable<PipelineAnalysisGradeRuleDefinition> rules)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredDomains = new HashSet<AnalysisGradeDomain>();
        var errors = new List<List<string>>();
        foreach (var rule in rules.Take(100))
        {
            var ruleErrors = new List<string>();
            ValidateRule(scope, rule, keys, configuredDomains, ruleErrors);
            errors.Add(ruleErrors);
        }
        return (errors, configuredDomains);
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

    // A rule grades exactly one thing: a metric, one severity's finding count, or the weighted
    // score of the open findings, which only a lower-is-better direction can grade.
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
        // A rule on findings (one severity's count, or a finding score, counted or weighted) may stop its
        // thresholds after B, C or D: everything beyond the last one is the next grade and never worse
        // (owner decision of 2026-10-25, recette R-73: "at least one high is a D, never an E nor an F").
        // A metric rule needs its five thresholds.
        var openTail = IsFindingScore(rule) || !string.IsNullOrWhiteSpace(rule.Severity);
        var all = new[] { rule.A, rule.B, rule.C, rule.D, rule.E };
        var defined = all.TakeWhile(value => value.HasValue).Count();
        var values = all.Take(defined).ToArray();
        var acceptable = openTail
            ? defined >= 2 && all.Skip(defined).All(value => !value.HasValue)
            : defined == all.Length;
        if (!acceptable || values.Any(value => !double.IsFinite(value!.Value)))
        {
            errors.Add(openTail
                ? $"analysis grading rule '{label}' must define finite A and B thresholds, then C, D and E in order without a gap (the grade beyond the last one is the next grade)."
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

/// <summary>What <see cref="PipelineAnalysisGradingValidator.Diagnose"/> found: the errors of the grading block itself,
/// and the errors of each rule (empty for a valid rule), in the order of the block's first 100 rules.</summary>
public sealed record PipelineAnalysisGradingDiagnosis(
    IReadOnlyList<string> GradingErrors,
    IReadOnlyList<IReadOnlyList<string>> RuleErrors);
