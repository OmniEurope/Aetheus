// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunResultsTabs
{
    /// <summary>
    /// The trend charts are wide (28rem by 12rem). OE draws a chart at its aspect ratio, square by
    /// default: a fixed CSS height left the square drawing spilling over the next section (measured on
    /// 2026-09-28 at 375px, 105px over the assembly table's title).
    /// </summary>
    private const double TrendChartAspectRatio = 28d / 12d;

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunDto Run { get; set; } = default!;
    [Parameter, EditorRequired] public PipelineRunMetrics Metrics { get; set; } = default!;
    [Parameter, EditorRequired] public IReadOnlyDictionary<int, List<TaskLogDto>> StepLogs { get; set; } = default!;
    [Parameter, EditorRequired] public IReadOnlyList<PipelineStepRunDto> LintSteps { get; set; } = default!;
    [Parameter] public bool HasLintTab { get; set; }
    [Parameter] public EventCallback<PipelineArtifactDto> ArtifactDownloadRequested { get; set; }
    [Parameter] public EventCallback<int> CoverageLogsRequested { get; set; }
    [Parameter] public EventCallback<int> LintLogsRequested { get; set; }

    /// <summary>The coverage gauge states its tier in words so colour is not the only
    /// cue.</summary>
    private string CoverageTierText(double rate) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L["CoverageTierTitle"],
            PipelineRunFormatting.FormatPercent(rate), L[PipelineRunFormatting.CoverageTierKey(rate)]);

    private IReadOnlyList<OmniChartPoint> CoverageLinePoints => OmniChartData.Indexed(Metrics.CoverageTrend, point => point.LinePct, point => point.Label);
    private IReadOnlyList<OmniChartPoint> CoverageBranchPoints => OmniChartData.Indexed(Metrics.CoverageTrend, point => point.BranchPct, point => point.Label);
    private IReadOnlyList<OmniChartPoint> ComplexityAveragePoints => OmniChartData.Indexed(Metrics.ComplexityTrend, point => point.AvgCc, point => point.Label);
    private IReadOnlyList<OmniChartPoint> ComplexityMaximumPoints => OmniChartData.Indexed(Metrics.ComplexityTrend, point => point.MaxCc, point => point.Label);
    private IReadOnlyList<OmniChartPoint> ComplexityCrapPoints => OmniChartData.Indexed(Metrics.ComplexityTrend, point => point.Crap ?? 0, point => point.Label);
}
