// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>Grade and gate all metrics before retaining project totals, worst-per-key rows,
/// policy-flagged rows and per-key extrema. Keeping extrema preserves minimum and maximum grading
/// results when stored metrics are read back.</summary>
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
