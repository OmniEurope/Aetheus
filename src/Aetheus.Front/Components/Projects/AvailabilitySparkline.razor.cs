// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects;

/// <summary>
/// S-UX-SPKL: a compact inline SVG sparkline of an app's trailing-24 h availability. Each sample renders
/// as a full-height bar - green when the probe was up, red when down - giving an at-a-glance uptime
/// pattern next to the numeric percentage in the supervision list. All coordinates are integers (no
/// culture-sensitive decimal separator, safe under fr-FR).
/// </summary>
public partial class AvailabilitySparkline
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public IReadOnlyList<AppHealthSampleDto> Samples { get; set; } = [];

    private const int Width = 96;
    // Cap the bar count so a busy 24 h window stays readable; evenly subsample when there are more.
    private const int MaxBars = 48;

    private readonly record struct Bar(int X, int W, bool IsUp);

    private List<Bar> _bars = [];

    /// <summary>What the bar means, as its accessible name and hover text.</summary>
    private string Summary => L["Uptime24hLegend"];

    protected override void OnParametersSet() => _bars = BuildBars();

    private List<Bar> BuildBars()
    {
        if (Samples.Count == 0) return [];

        var ordered = Samples.OrderBy(s => s.Timestamp).ToList();
        var columns = Math.Min(ordered.Count, MaxBars);
        var bars = new List<Bar>(columns);
        for (var i = 0; i < columns; i++)
        {
            // Even integer partition of the fixed width, and even subsampling when Count > MaxBars.
            var x = i * Width / columns;
            var next = (i + 1) * Width / columns;
            var sampleStart = (int)((long)i * ordered.Count / columns);
            var sampleEnd = (int)((long)(i + 1) * ordered.Count / columns);
            var isUp = ordered.GetRange(sampleStart, Math.Max(1, sampleEnd - sampleStart)).All(sample => sample.IsUp);
            bars.Add(new Bar(x, Math.Max(1, next - x), isUp));
        }
        return bars;
    }
}
