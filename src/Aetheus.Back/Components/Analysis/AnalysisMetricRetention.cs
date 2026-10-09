// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// PLAN-013 lot 3 (recette R2-115): the metrics of a report that are written to <c>AnalysisMetrics</c>.
/// Every project total (no file, no symbol) is kept. Per-file and per-symbol rows are kept only when they
/// are among the <see cref="WorstPerKey"/> worst of their key, when they made a policy warn or block, or
/// when they are the lowest or highest value of their key. The report is graded and gated on all of its
/// metrics before this selection, and the kept extremes mean a grading rule's minimum or maximum read
/// back from the stored rows is the same as the one the grade was computed from.
/// </summary>
internal static class AnalysisMetricRetention
{
    public const int WorstPerKey = 50;

    public static IReadOnlyList<AnalysisMetric> Select(
        IReadOnlyCollection<AnalysisMetric> metrics,
        IReadOnlySet<AnalysisMetric> flagged)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(flagged);
        var kept = new HashSet<AnalysisMetric>(flagged, ReferenceEqualityComparer.Instance);
        foreach (var group in metrics.Where(IsDetail).GroupBy(metric => metric.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderByDescending(Severity).ToList();
            kept.UnionWith(ordered.Take(WorstPerKey));
            kept.Add(group.MinBy(metric => metric.Value)!);
            kept.Add(group.MaxBy(metric => metric.Value)!);
        }
        return [.. metrics.Where(metric => !IsDetail(metric) || kept.Contains(metric))];
    }

    /// <summary>A row about one file or one symbol, as opposed to a project or language total.</summary>
    public static bool IsDetail(AnalysisMetric metric) => metric.FilePath != null || metric.Symbol != null;

    /// <summary>
    /// How bad a value is for its key: the higher, the worse. An informational metric has no better
    /// side, its largest values come first.
    /// </summary>
    private static double Severity(AnalysisMetric metric) =>
        metric.Direction == AnalysisMetricDirection.HigherIsBetter ? -metric.Value : metric.Value;
}
