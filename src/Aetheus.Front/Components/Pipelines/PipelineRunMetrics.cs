// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Coverage / code-quality metrics and trend series for a pipeline run's Coverage and Code Quality
/// tabs. Extracted from <see cref="PipelineRun"/> as its own collaborator (run-metrics concern) so the
/// component stays within the file-size budget. The <c>Has*</c>/<c>Metric</c> helpers are static reads
/// over the run DTO; the trend lists are fetched once per load via <see cref="LoadAsync"/>.
/// </summary>
public sealed class PipelineRunMetrics
{
    // L: cyclomatic-complexity threshold above which a method is flagged "high" on the Code Quality tab.
    public const int ComplexityHighThreshold = 10;

    public sealed record CoverageTrendVm(string Label, double LinePct, double BranchPct);
    public sealed record ComplexityTrendVm(string Label, double AvgCc, double MaxCc, double? Crap);

    // K / S-FEAT-C4R2: trends across the pipeline's recent runs (oldest->newest), shown as line charts.
    public IReadOnlyList<CoverageTrendVm> CoverageTrend { get; private set; } = [];
    public IReadOnlyList<ComplexityTrendVm> ComplexityTrend { get; private set; } = [];

    /// <summary>True when the run carries (or produced) coverage data, so the Coverage tab is shown.</summary>
    public static bool HasCoverage(PipelineRunDto? run) =>
        run?.CoverageSummary is not null
        || run?.Steps.Any(s => s.StageName.Contains("Coverage", StringComparison.OrdinalIgnoreCase) && !s.IsSystem) == true;

    /// <summary>True when the run produced complexity metrics, so the Code Quality tab is shown.</summary>
    public static bool HasComplexity(PipelineRunDto? run) =>
        run?.Metrics.Any(m => m.Key.StartsWith("complexity.", StringComparison.Ordinal)) == true;

    /// <summary>A single run metric value by key (e.g. <c>complexity.cyclomatic.avg</c>), or null.</summary>
    public static double? Metric(PipelineRunDto? run, string key) =>
        run?.Metrics.FirstOrDefault(m => m.Key == key)?.Value;

    /// <summary>Fetch both trend series (each gated by the run actually having that data).
    /// Best-effort: a transient API failure leaves the corresponding series empty rather than
    /// throwing.</summary> <remarks>the two series are independent, so they are fetched
    /// together.</remarks>
    public Task LoadAsync(ApiClient api, int runId, PipelineRunDto? run) =>
        Task.WhenAll(LoadCoverageTrendAsync(api, runId, run), LoadComplexityTrendAsync(api, runId, run));

    private async Task LoadCoverageTrendAsync(ApiClient api, int runId, PipelineRunDto? run)
    {
        if (!HasCoverage(run)) return;
        try
        {
            var points = await api.Analysis.GetCoverageTrendAsync(runId);
            CoverageTrend = points
                .Select(p => new CoverageTrendVm($"#{p.RunId}", Math.Round(p.LineRate * 100, 1), Math.Round(p.BranchRate * 100, 1)))
                .ToList();
        }
        catch (HttpRequestException) { CoverageTrend = []; }
    }

    private async Task LoadComplexityTrendAsync(ApiClient api, int runId, PipelineRunDto? run)
    {
        if (!HasComplexity(run)) return;
        try
        {
            var points = await api.Analysis.GetComplexityTrendAsync(runId);
            ComplexityTrend = points
                .Select(p => new ComplexityTrendVm($"#{p.RunId}", Math.Round(p.AvgCyclomatic, 1), Math.Round(p.MaxCyclomatic, 1),
                    p.CrapAvg.HasValue ? Math.Round(p.CrapAvg.Value, 1) : null))
                .ToList();
        }
        catch (HttpRequestException) { ComplexityTrend = []; }
    }
}
