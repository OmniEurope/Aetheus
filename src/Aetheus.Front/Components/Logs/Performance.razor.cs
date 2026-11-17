// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Logs;

public partial class Performance : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private ClientApiTimings BrowserTimings { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private GridExportContext ExportContext { get; set; } = default!;

    /// <summary>Calls of this session shown, slowest first.</summary>
    private const int BrowserCallsShown = 20;

    private ApiPerformanceReportDto? _report;
    private TaskIdentityBackfillDto? _taskIdentity;
    private PipelineRunIdentityBackfillDto? _runIdentity;
    private List<DatabaseTableVolumeDto> _tables = [];
    private List<ClientApiCall> _browserCalls = [];
    private AetheusDataGrid<ApiCallTimingDto>? _slowestGrid;
    private AetheusDataGrid<ApiEndpointTimingDto>? _endpointsGrid;
    private AetheusDataGrid<ClientApiCall>? _browserGrid;

    /// <summary>The date column shows local time, so its range filter and sort read the same local
    /// instant rather than the UTC one the API sends.</summary>
    private static readonly Func<ApiCallTimingDto, object?> LocalAt = call => call.At.ToLocalTime();
    private bool _loadFailed;

    // Refresh from coalesced ApiPerformance admin-hub events and coalesce bursts on the client too.
    private AdminEntitySubscription? _realtime;
    private readonly TrailingReloadCoalescer _realtimeReload = new(500);

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _realtime = new AdminEntitySubscription(HubFactory);
        // Not awaited: the figures are on screen now, a slow hub negotiation must not hold them back.
        _ = _realtime.StartAsync(AdminEntities.ApiPerformance,
            () => InvokeAsync(() => _realtimeReload.RequestAsync(ReloadFromPushAsync)));
    }

    private async Task LoadAsync()
    {
        _loadFailed = false;
        try
        {
            _report = await Api.Security.GetApiPerformanceAsync() ?? new ApiPerformanceReportDto();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _loadFailed = true;
        }
        finally
        {
            SnapshotBrowserCalls();
        }
        await LoadTaskIdentityAsync();
        await LoadTablesAsync();
    }

    /// <summary>A failed read keeps the last rows: the report does not depend on the catalog read.</summary>
    private async Task LoadTablesAsync()
    {
        try
        {
            _tables = await Api.Security.GetDatabaseTablesAsync() ?? _tables;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // The grid keeps what it showed.
        }
    }

    private static string NumberText(long? value) =>
        value is { } number ? number.ToString("N0", CultureInfo.CurrentCulture) : "-";

    private static string KeyPercentText(double? percent) =>
        percent is { } value ? value.ToString("0.0000", CultureInfo.CurrentCulture) : "-";

    /// <summary>A failed read leaves the last figure (or no tile): the report does not depend on it.</summary>
    private async Task LoadTaskIdentityAsync()
    {
        try
        {
            _taskIdentity = await Api.Pipelines.GetTaskIdentityBackfillAsync() ?? _taskIdentity;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // The tile keeps what it showed; the next push reads it again.
        }

        try
        {
            _runIdentity = await Api.Pipelines.GetPipelineRunIdentityBackfillAsync() ?? _runIdentity;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Same: one tile failing leaves the other.
        }
    }

    /// <summary>
    /// One column per key figure (the four of the report, plus the identity backfill tiles when they are read), so a
    /// window wide enough puts them in one row above the grid; page rule 3.page-scroll-with-grid, the grid keeps its
    /// ten rows without the page scrolling. A phone keeps its single scrolling row (.tiles-scroll-row).
    /// </summary>
    private int TileColumns => Math.Min(6, 4 + (_taskIdentity is not null ? 1 : 0) + (_runIdentity is not null ? 1 : 0));

    private string TaskIdentityRemainingText =>
        _taskIdentity?.Remaining.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;

    private string TaskIdentityDetailText => L["TaskIdentityBackfillDetail", _taskIdentity?.Total ?? 0];

    private string RunIdentityRemainingText =>
        _runIdentity?.Remaining.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;

    private string RunIdentityDetailText => L["PipelineRunIdentityBackfillDetail", _runIdentity?.Total ?? 0];

    /// <summary>A push re-reads the report in place; a failure keeps the figures on screen for the next push.</summary>
    internal async Task ReloadFromPushAsync()
    {
        try
        {
            var report = await Api.Security.GetApiPerformanceAsync() ?? new ApiPerformanceReportDto();
            // The grids note the rows they hold before the new ones land, so a new sample reads
            // bold for a few seconds.
            if (_slowestGrid is not null) await _slowestGrid.Refresh();
            if (_endpointsGrid is not null) await _endpointsGrid.Refresh();
            if (_browserGrid is not null) await _browserGrid.Refresh();
            _report = report;
            _loadFailed = false;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return;
        }
        await LoadTaskIdentityAsync();
        SnapshotBrowserCalls();
        StateHasChanged();
    }

    private void SnapshotBrowserCalls() =>
        _browserCalls = [.. BrowserTimings.Snapshot()
            .OrderByDescending(call => call.DurationMs)
            .Take(BrowserCallsShown)];

    /// <summary>Says how much the figures cover: 24 hours, or less since the instance started, and
    /// whether the cap dropped the oldest samples. A number without its window invites the wrong
    /// conclusion.</summary>
    private string WindowText => _report is { Since: { } since }
        ? string.Format(CultureInfo.CurrentCulture, L["PerformanceWindow"], since.ToLocalTime().ToString("g"))
          + (_report.Truncated ? " " + L["PerformanceTruncated"] : string.Empty)
        : L["PerformanceNoSamples"];

    private string SampleCountText => (_report?.SampleCount ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    private string RoutesCountText => (_report?.Endpoints.Count ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    private ApiEndpointTimingDto? SlowestRoute => _report?.Endpoints.MaxBy(endpoint => endpoint.P95Ms);

    private string SlowestP95Text => SlowestRoute is { } route ? Milliseconds(route.P95Ms) : "-";

    private string? SlowestRouteText => SlowestRoute is { } route ? string.Concat(route.Method, " ", route.Route) : null;

    private ApiCallTimingDto? SlowestCall => _report?.Slowest.MaxBy(call => call.DurationMs);

    private string SlowestCallText => SlowestCall is { } call ? Milliseconds(call.DurationMs) : "-";

    private string? SlowestCallRouteText => SlowestCall is { } call ? string.Concat(call.Method, " ", call.Route) : null;

    /// <summary>The tab slugs, in the order of the tabs.</summary>
    private static readonly string[] TabSlugs = ["slowest", "routes", "browser", "tables"];

    /// <summary> The header of each table's export, the page's source and address, then the server
    /// window the page writes above the tabs, so a file read alone still says since when the
    /// figures count. </summary>
    private IReadOnlyList<OmniTableExportField> ExportFields =>
        [.. ExportContext.Fields(), .. WindowFields(_report ?? new ApiPerformanceReportDto(), key => L[key].Value)];

    /// <summary>The server window as export header lines: start in UTC, requests measured, cap reached.</summary>
    internal static IReadOnlyList<OmniTableExportField> WindowFields(ApiPerformanceReportDto report, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            new(text("PerformanceExportSince"), report.Since is { } since ? AppTelemetryMarkdownExport.IsoUtc(since) : text("ExportNone")),
            new(text("PerformanceExportSamples"), report.SampleCount.ToString(CultureInfo.InvariantCulture)),
            new(text("PerformanceExportTruncated"), text(report.Truncated ? "Yes" : "No"))
        ];
    }

    // Recette R1-96: the column title carries the unit, so a cell is the bare rounded number.
    internal static string WholeMilliseconds(double value) =>
        Math.Round(value).ToString("N0", CultureInfo.CurrentCulture);

    private static string Milliseconds(double value) =>
        value >= 1000
            ? (value / 1000).ToString("0.00", CultureInfo.CurrentCulture) + " s"
            : Math.Round(value).ToString(CultureInfo.CurrentCulture) + " ms";

    private static string FormatBytes(long bytes) => DisplayFormatting.Size(bytes);

    public async ValueTask DisposeAsync()
    {
        if (_realtime is not null)
            await _realtime.DisposeAsync();
    }
}
