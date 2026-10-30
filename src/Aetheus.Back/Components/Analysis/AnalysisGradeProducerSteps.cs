// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis;

/// <summary>Names, for a grading rule, the pipeline step that publishes the evidence the rule reads, so a required
/// measure nobody observed can say which step did not produce it (recette R-73, R-74). Read from the pipeline
/// definition, not from the run: a step a condition skipped has no task, and it is exactly the one to name.</summary>
internal static class AnalysisGradeProducerSteps
{
    private const int MaxNamed = 3;

    /// <summary>The name of the step or steps of the pipeline that publish reports of the category the rule is bound to;
    /// null when the rule has no category (it reads every report of its scope) or the pipeline holds no such step.</summary>
    public static string? Find(
        IEnumerable<PipelineStageDefinition> stages,
        PipelineAnalysisGradeRuleDefinition rule)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(rule);
        if (!PipelineAnalysisGateRuleValidator.TryParseEnum<AnalysisCategory>(rule.Category, out var parsed)
            || parsed is not { } category)
            return null;
        var readsAMetric = !string.IsNullOrWhiteSpace(rule.Metric);
        var names = stages
            .SelectMany(stage => stage.Steps)
            .Where(step => !string.IsNullOrWhiteSpace(step.Name) && Produces(step, category, readsAMetric))
            .Select(step => step.Name)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxNamed)
            .ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static bool Produces(PipelineStepDefinition step, AnalysisCategory category, bool readsAMetric) =>
        step.Type?.Trim().ToLowerInvariant() switch
        {
            "coverage" => category == AnalysisCategory.Coverage,
            // The complexity step publishes the complexity metrics and the architecture reports.
            "complexity" => category is AnalysisCategory.CodeQuality or AnalysisCategory.Architecture,
            // A lint step publishes findings only: it never produces the value of a metric.
            "lint" => !readsAMetric
                && category == (string.Equals(step.AnalysisCategory?.Trim(), "accessibility", StringComparison.OrdinalIgnoreCase)
                    ? AnalysisCategory.Accessibility
                    : AnalysisCategory.CodeQuality),
            // A scanner publishes findings; the only metric it publishes is the duplication of the clone detector.
            "scanner" => (!readsAMetric || category == AnalysisCategory.Duplication)
                && !string.IsNullOrWhiteSpace(step.Scanner)
                && string.Equals(ScannerManifestCatalog.Find(step.Scanner)?.Category, category.ToString(), StringComparison.OrdinalIgnoreCase),
            _ => false
        };
}
