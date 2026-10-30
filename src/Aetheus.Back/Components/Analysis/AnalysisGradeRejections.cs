// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// The measures that stand for a grading problem (owner decision after run #2958, "a problem is an F"): a rule the
/// validator rejects, or an <c>analysis_grading</c> block that cannot be used at all, is a required measure graded F
/// and observed, so it counts in the grade, carrying the validator's message so the gate and the Quality page say why.
/// </summary>
internal static class AnalysisGradeRejections
{
    /// <summary>The key of the measure that stands for the grading block itself.</summary>
    public const string GradingKey = "analysis_grading";

    private const int MaxKeyLength = 200;
    private const int MaxReasonLength = 2000;

    public static AnalysisGradeMeasureDto Rule(
        PipelineAnalysisGradeRuleDefinition rule,
        IEnumerable<string> errors,
        string scope)
    {
        var domain = PipelineAnalysisGradingValidator.TryParseDomain(rule.Domain, out var parsed)
            ? parsed!.Value
            : ScopeDomain(scope);
        var key = string.IsNullOrWhiteSpace(rule.Key) ? "<missing>" : rule.Key;
        return Measure(key, domain, errors);
    }

    public static AnalysisGradeMeasureDto Grading(
        IEnumerable<string> errors,
        IReadOnlySet<AnalysisGradeDomain> requiredDomains,
        string scope) =>
        Measure(GradingKey, requiredDomains.Count > 0 ? requiredDomains.Min() : ScopeDomain(scope), errors);

    // A rule whose domain is unreadable still needs a domain to be listed in: the gate's own.
    private static AnalysisGradeDomain ScopeDomain(string scope) =>
        scope == AnalysisGateScopes.Security ? AnalysisGradeDomain.Security : AnalysisGradeDomain.CodeQuality;

    private static AnalysisGradeMeasureDto Measure(string key, AnalysisGradeDomain domain, IEnumerable<string> errors)
    {
        var reason = string.Join(" ", errors);
        return new AnalysisGradeMeasureDto
        {
            Key = key.Length > MaxKeyLength ? key[..MaxKeyLength] : key,
            Domain = domain,
            Required = true,
            Observed = true,
            Grade = AnalysisGrade.F,
            Rejection = reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason
        };
    }
}
