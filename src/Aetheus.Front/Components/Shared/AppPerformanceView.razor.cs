// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Shared;

public partial class AppPerformanceView
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>Recette R-476: nearest-rank percentiles are requests that happened, so under this many
    /// calls the 95th (then the 99th) is the slowest call itself.</summary>
    internal const int P95MinimumCalls = 20;
    internal const int P99MinimumCalls = 100;

    [Parameter] public int AppId { get; set; }

    /// <summary>Names the application in the export; the identifier alone when absent.</summary>
    [Parameter] public string? AppName { get; set; }

    private AppPerformanceReportDto? _report;
    private bool _loading = true;
    private bool _loadFailed;
    private bool _explorerExpanded;
    private int _lastAppId = -1;

    protected override async Task OnParametersSetAsync()
    {
        if (AppId == _lastAppId) return;
        _lastAppId = AppId;
        _explorerExpanded = false;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _loadFailed = false;
        try
        {
            _report = await Api.Monitoring.GetAppPerformanceAsync(AppId);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _report = null;
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Recette R2-010: the date the application last sent its figures, the value of the first tile.</summary>
    private string LastSentText => _report?.MeasuredAt is { } measuredAt
        ? measuredAt.ToString("g", CultureInfo.CurrentCulture)
        : L["Never"];

    /// <summary>The figures are the application's own window as of its last export: the tile's detail
    /// says, when the package says it (1.0.0, R-476), since when the requests are counted, the window
    /// restarting with the application.</summary>
    private string WindowText => _report switch
    {
        { MeasuredAt: { } measuredAt, WindowSince: { } since } => string.Format(
            CultureInfo.CurrentCulture, L["AppPerformanceWindowSince"],
            since.ToString("g", CultureInfo.CurrentCulture), SpanText(measuredAt - since)),
        { MeasuredAt: not null } => L["AppPerformanceWindow"],
        _ => string.Empty
    };

    /// <summary>"3 h 05 min" or "12 min": how long the counted requests span.</summary>
    internal static string SpanText(TimeSpan span) => span.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h {span.Minutes:00} min")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min");

    /// <summary>True when the route counts too few calls for the percentile to be anything but its
    /// maximum. A count the export did not carry says nothing, so the figure is left as it is.</summary>
    internal static bool IsOrderOfMagnitude(AppRouteTimingDto route, int minimumCalls) =>
        route.Count is { } count && count < minimumCalls;

    private string FewCallsHint(int minimumCalls) =>
        string.Format(CultureInfo.CurrentCulture, L["AppPerformanceFewCalls"], minimumCalls);

    // The timing columns show "12 ms" or "1.20 s"; the export writes the milliseconds, as numbers.
    private static readonly Func<AppRouteTimingDto, object?> P50Export = route => AppTelemetryMarkdownExport.MillisecondsExport(route.P50Ms);
    private static readonly Func<AppRouteTimingDto, object?> P95Export = route => AppTelemetryMarkdownExport.MillisecondsExport(route.P95Ms);
    private static readonly Func<AppRouteTimingDto, object?> P99Export = route => AppTelemetryMarkdownExport.MillisecondsExport(route.P99Ms);
    private static readonly Func<AppRouteTimingDto, object?> MaxExport = route => AppTelemetryMarkdownExport.MillisecondsExport(route.MaxMs);

    private string ExportTitle => AppTelemetryMarkdownExport.Title(L["AppPerformanceExportTitle"], AppId, AppName);

    /// <summary>Recette R-474: the header of the export, everything the tab shows above the table.</summary>
    private IReadOnlyList<OmniTableExportField> ExportFields => AppTelemetryMarkdownExport.PerformanceFields(
        AppId, AppName, _report ?? new AppPerformanceReportDto(), key => L[key].Value);

    /// <summary>Recette R2-036: <c>aetheus-performance</c>, then OE's generation time and the extension.</summary>
    private string ExportFileName => ExportFileNames.Stem(AppName, $"app-{AppId}", "performance");

    private string RoutesCountText => (_report?.Routes.Count ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    private string TotalRequestsText =>
        (_report?.Routes.Sum(route => route.Count ?? 0) ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    private AppRouteTimingDto? SlowestRoute => _report?.Routes
        .Where(route => route.P95Ms is not null)
        .MaxBy(route => route.P95Ms);

    private string SlowestP95Text => Milliseconds(SlowestRoute?.P95Ms);

    private string? SlowestRouteText => SlowestRoute is { } route
        ? string.Concat(route.Method, " ", route.Route)
        : null;

    internal static string Milliseconds(double? value) => value switch
    {
        null => "-",
        >= 1000 => (value.Value / 1000).ToString("0.00", CultureInfo.CurrentCulture) + " s",
        _ => Math.Round(value.Value).ToString(CultureInfo.CurrentCulture) + " ms"
    };
}
