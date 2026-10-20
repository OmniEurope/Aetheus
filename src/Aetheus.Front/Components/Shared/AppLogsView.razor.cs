// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.ExceptionServices;

namespace Aetheus.Front.Components.Shared;

public partial class AppLogsView : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter] public int AppId { get; set; }

    /// <summary>Name the application in the export header and filename so the file identifies its
    /// source; use its identifier when no name is available.</summary>
    [Parameter] public string? AppName { get; set; }

    /// <summary>The period the grid and the export read, in hours back from now.</summary>
    private const int HistoryHours = 24;

    /// <summary>The app's project: the scope of the <c>AppTelemetryChanged</c> push that reloads this view.</summary>
    [Parameter] public int ProjectId { get; set; }

    /// <summary>List OTLP severity classes in severity order so the filter matches the API's
    /// numeric ranges and preserves their meaning.</summary>
    internal static readonly string[] SeverityClasses = ["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"];

    // Recette R3-35: clicking a line opens that exact entry, its message with its values as the cell
    // shows it, and its attributes.
    private Task OpenEntryAsync(AppLogEntryDto entry) => Logs.LogEntryDialog.OpenAsync(Dialog, L,
        LogMessageTemplate.Render(entry.Body, entry.AttributesJson), entry.AttributesJson, L["LogAttributes"]);

    // What the Message column writes in an export: the message with its values, as the cell shows it.
    // Static, so every render hands the same delegate.
    private static readonly Func<AppLogEntryDto, object?> MessageExport =
        entry => LogMessageTemplate.Render(entry.Body, entry.AttributesJson);

    // Share localized severity names between badges, filters and exports; construct delegates once
    // so renders retain stable references.
    private Func<AppLogEntryDto, object?> _severityExport = default!;
    private Func<string, string> _severityFilterLabel = default!;

    private AetheusDataGrid<AppLogEntryDto>? _grid;
    private List<AppLogEntryDto> _items = [];
    private int _total;
    private bool _loading;
    private bool _loadFailed;
    private readonly PushRefresh _pushRefresh = new();
    private int? _loadedAppId;

    // Subscribe to the project's AppTelemetryChanged events so newly ingested logs refresh the grid
    // with its current filters.
    private EntityOperationalFeed? _liveFeed;
    internal EntityOperationalFeed? LiveFeed => _liveFeed;

    protected override void OnInitialized()
    {
        _severityExport = entry => AppTelemetryMarkdownExport.SeverityExport(entry, Text);
        _severityFilterLabel = severityClass => AppTelemetryMarkdownExport.SeverityClassLabel(severityClass, Text);
    }

    protected override async Task OnParametersSetAsync()
    {
        _liveFeed ??= new EntityOperationalFeed(HubFactory);
        _ = _liveFeed.StartAsync(ResourceType.Project, OperationalRealtimeEvents.AppTelemetryChanged, ProjectId,
            () => InvokeAsync(RefreshFromPushAsync));
        if (_loadedAppId == AppId) return;
        var switched = _loadedAppId is not null;
        _loadedAppId = AppId;
        // The first load comes from the grid itself; another app on the same instance starts over from
        // its first block, with the filters the reader set.
        if (switched && _grid is not null)
        {
            _items = [];
            _total = 0;
            await _grid.Reload();
        }
    }

    /// <summary> Every block the grid asks for goes to the API with the column sort and the header
    /// filters, so the count and the rows describe the whole log of the app over the last 24 hours.
    /// </summary>
    private async Task LoadDataAsync(GridLoadArgs args)
    {
        var appId = AppId;
        var fromPush = _pushRefresh.Running;
        if (!fromPush) _loading = true;
        try
        {
            var result = await ReadAsync(appId, args);
            if (AppId != appId) return;
            _items = result.Items;
            _total = result.TotalCount;
            _loadFailed = false;
        }
        catch (HttpRequestException)
        {
            if (AppId != appId) return;
            // A push that fails keeps the rows on screen for the next push. Any other failed call is
            // not "no logs yet": saying so hid every outage of the endpoint behind the empty state.
            if (fromPush) return;
            _items = [];
            _total = 0;
            _loadFailed = true;
        }
        finally
        {
            if (!fromPush && AppId == appId) _loading = false;
        }
    }

    /// <summary> The pages of an export, read from the API with the sort and the column filters of
    /// the grid, without touching the rows it shows (the grid's load replaces them). A failed page
    /// fails the export: no file, <see cref="OnExportFailed"/> says so. </summary>
    private async Task<OmniDataGridResult<AppLogEntryDto>> LoadExportPageAsync(OmniDataGridLoadRequest request)
    {
        var result = await ReadAsync(AppId, request.ToGridLoadArgs());
        return new OmniDataGridResult<AppLogEntryDto>(result.Items, result.TotalCount);
    }

    private Task<PaginatedResult<AppLogEntryDto>> ReadAsync(int appId, GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(defaultPageSize: 50);
        var (sortBy, sortDescending) = args.ToSortRequest("Timestamp", fallbackDescending: true);
        return Api.Monitoring.GetAppLogsAsync(appId, hours: HistoryHours, page: page, pageSize: pageSize,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    /// <summary>A push refreshes the grid in place (rows, scroll position and filters
    /// kept).</summary>
    private async Task RefreshFromPushAsync()
    {
        if (_grid is null) return;
        await _pushRefresh.RunAsync(_grid.Refresh);
        StateHasChanged();
    }

    private Task RetryAsync()
    {
        _loadFailed = false;
        return _grid?.Reload() ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_liveFeed is not null)
            await _liveFeed.DisposeAsync();
    }

    private string ExportTitle => AppTelemetryMarkdownExport.Title(L["LogsExportTitle"], AppId, AppName);

    private IReadOnlyList<OmniTableExportField> ExportFields =>
        AppTelemetryMarkdownExport.LogsFields(AppId, AppName, HistoryHours, key => L[key].Value);

    /// <summary><c>aetheus-logs</c>, then OE's generation time and the extension.</summary>
    private string ExportFileName => ExportFileNames.Stem(AppName, $"app-{AppId}", "logs");

    /// <summary>A page that could not be read: no file was produced, the toast says so. Any other
    /// failure is not an unreadable page and goes to the error boundary as before.</summary>
    private void OnExportFailed(Exception exception)
    {
        if (exception is not HttpRequestException)
            ExceptionDispatchInfo.Capture(exception).Throw();
        Toast.Error("LogsExportFailed");
    }

    private static OmniTone SeverityBadge(int severityNumber) => severityNumber switch
    {
        >= 21 => OmniTone.Danger,   // FATAL
        >= 17 => OmniTone.Danger,   // ERROR
        >= 13 => OmniTone.Warning,  // WARN
        >= 9 => OmniTone.Accent,      // INFO
        >= 5 => OmniTone.Neutral,     // DEBUG
        _ => OmniTone.Neutral     // TRACE / unspecified
    };

    private string SeverityLabel(AppLogEntryDto entry) => AppTelemetryMarkdownExport.SeverityLabel(entry, Text);

    private string Text(string key) => L[key].Value;
}
