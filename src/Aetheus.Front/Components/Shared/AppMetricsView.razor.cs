using System.Globalization;
using System.Text.Json;
// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public partial class AppMetricsView
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public int AppId { get; set; }

    private sealed record ChartPoint(DateTime Timestamp, double Value, double? P95, double? Min, double? Max);
    private sealed record BandPoint(DateTime Timestamp, double Min, double Max);
    private sealed record P95Point(DateTime Timestamp, double P95);
    /// <summary>Use an empty key for an attribute-free series because a null dropdown value makes
    /// OE treat the whole option as its value.</summary>
    private sealed record GroupOption(string Key, string Label)
    {
        public string? AttributesJson => Key.Length == 0 ? null : Key;
    }
    private sealed record PeriodOption(string Label, int Hours);

    private List<string> _names = [];
    private string? _selectedMetric;
    private List<GroupOption> _groups = [];
    private string _selectedGroupKey = string.Empty;
    private bool _namesLoadFailed;
    // The metric list and the series list each say they are loading instead of showing "no metric
    // yet" or an empty picker while their call is in flight.
    private bool _namesLoading = true;
    private bool _groupsLoading;
    private int _hours = 24;
    private bool _loading;
    private List<ChartPoint> _points = [];
    private List<BandPoint> _bandPoints = [];
    private List<P95Point> _p95Points = [];
    private bool _showMarkers;
    private double _average;
    private double _peak;
    private string? _unit;
    private int _lastAppId = -1;

    // Y-axis title: the metric's unit when the series carries one (S-DES-YAXU), else the metric name.
    private string AxisTitle => string.IsNullOrWhiteSpace(_unit) ? (_selectedMetric ?? string.Empty) : _unit;

    // Time labels only need the date once the window spans more than a day.
    private string TimeLabel(DateTime value) => _hours <= 24 ? value.ToString("t") : DateDisplay.DayMonthTime(value);
    private IReadOnlyList<OmniChartPoint> MetricPoints => OmniChartData.Timed(_points, point => point.Timestamp, point => point.Value, point => TimeLabel(point.Timestamp));
    private IReadOnlyList<OmniChartPoint> MinimumPoints => OmniChartData.Timed(_bandPoints, point => point.Timestamp, point => point.Min, point => TimeLabel(point.Timestamp));
    private IReadOnlyList<OmniChartPoint> MaximumPoints => OmniChartData.Timed(_bandPoints, point => point.Timestamp, point => point.Max, point => TimeLabel(point.Timestamp));
    private IReadOnlyList<OmniChartPoint> P95Points => OmniChartData.Timed(_p95Points, point => point.Timestamp, point => point.P95, point => TimeLabel(point.Timestamp));

    private readonly List<PeriodOption> _periods =
    [
        new("24h", 24), new("7d", 168), new("90d", 2160)
    ];

    protected override async Task OnParametersSetAsync()
    {
        if (AppId == _lastAppId) return;
        _lastAppId = AppId;
        await LoadNamesAsync();
    }

    private async Task LoadNamesAsync()
    {
        // A failed call is an error, never "no metrics yet".
        _namesLoadFailed = false;
        _namesLoading = true;
        try { _names = await Api.Monitoring.GetAppMetricNamesAsync(AppId); }
        catch (HttpRequestException)
        {
            _names = [];
            _namesLoadFailed = true;
        }
        finally { _namesLoading = false; }
        _selectedMetric = _names.FirstOrDefault();
        await LoadGroupsAsync();
    }

    private async Task OnMetricChanged()
    {
        await LoadGroupsAsync();
    }

    /// <summary>
    /// A metric name alone is not a series: an attribute set (for example one HTTP route) distinguishes
    /// logically separate series sharing the same name. Loads the distinct attribute combinations so the
    /// user can pick one instead of everything getting plotted as a single misleading merged line.
    /// </summary>
    private async Task LoadGroupsAsync()
    {
        if (string.IsNullOrEmpty(_selectedMetric))
        {
            _groups = [];
            _selectedGroupKey = string.Empty;
            await LoadSeriesAsync();
            return;
        }

        List<string?> raw;
        _groupsLoading = true;
        try { raw = await Api.Monitoring.GetAppMetricSeriesGroupsAsync(AppId, _selectedMetric); }
        catch (HttpRequestException) { raw = []; }
        finally { _groupsLoading = false; }
        _groups = raw
            .Select(json => new GroupOption(json ?? string.Empty, string.IsNullOrWhiteSpace(json) ? L["AllAttributes"] : FormatAttributesLabel(json)))
            .ToList();
        _selectedGroupKey = _groups.FirstOrDefault()?.Key ?? string.Empty;
        await LoadSeriesAsync();
    }

    private static string FormatAttributesLabel(string? attributesJson)
    {
        if (string.IsNullOrWhiteSpace(attributesJson))
            return string.Empty;
        try
        {
            var pairs = JsonSerializer.Deserialize<Dictionary<string, string>>(attributesJson);
            return pairs is null or { Count: 0 }
                ? attributesJson
                : string.Join(", ", pairs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        }
        catch (JsonException)
        {
            return attributesJson;
        }
    }

    private async Task LoadSeriesAsync()
    {
        if (string.IsNullOrEmpty(_selectedMetric))
        {
            ApplySeries(null);
            return;
        }

        _loading = true;
        MetricSeriesDto? series = null;
        var attributes = _selectedGroupKey.Length == 0 ? null : _selectedGroupKey;
        try { series = await Api.Monitoring.GetAppMetricSeriesAsync(AppId, _selectedMetric, attributes, _hours); }
        catch (HttpRequestException) { /* keep last */ }

        ApplySeries(series);
        _loading = false;
    }

    /// <summary>
    /// Turns the fetched series into what the chart binds. Separated from the fetch so the decisions
    /// it makes - which optional series are safe to plot, how dense the axis may get - can be
    /// exercised independently from the chart renderer.
    /// </summary>
    private void ApplySeries(MetricSeriesDto? series)
    {
        _unit = series?.Unit;
        _points = series?.Points
            .Select(p => new ChartPoint(
                p.Timestamp,
                Math.Round(p.Value, 3),
                p.P95.HasValue ? Math.Round(p.P95.Value, 3) : null,
                p.Min.HasValue ? Math.Round(p.Min.Value, 3) : null,
                p.Max.HasValue ? Math.Round(p.Max.Value, 3) : null))
            .ToList() ?? [];
        // The chart throws on a bound double? series containing a null - keep only the points that actually
        // carry the optional value instead of hiding the whole band/line for one point missing it.
        _bandPoints = _points
            .Where(p => p.Min.HasValue && p.Max.HasValue)
            .Select(p => new BandPoint(p.Timestamp, p.Min!.Value, p.Max!.Value))
            .ToList();
        _p95Points = _points
            .Where(p => p.P95.HasValue)
            .Select(p => new P95Point(p.Timestamp, p.P95!.Value))
            .ToList();
        // Markers help read a sparse series and turn a dense one into a solid blob.
        _showMarkers = _points.Count <= 30;
        _average = _points.Count > 0 ? Math.Round(_points.Average(p => p.Value), 3) : 0;
        _peak = _points.Count > 0 ? _points.Max(p => p.Max ?? p.Value) : 0;
    }

    private Task OnFilterChanged() => LoadSeriesAsync();

    // Large counters (bytes, allocations) are unreadable in full; keep small values precise.
    private static string FormatValue(double value) => Math.Abs(value) switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "G",
        >= 1_000_000 => (value / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1_000).ToString("0.##", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("0.###", CultureInfo.InvariantCulture)
    };
}
