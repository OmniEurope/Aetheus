// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.ExceptionServices;

namespace Aetheus.Front.Components.Shared;

public partial class AppErrorsView : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    [Parameter] public int AppId { get; set; }

    /// <summary>The application's name, written in the export's header (its id alone when
    /// absent).</summary>
    [Parameter] public string? AppName { get; set; }

    /// <summary>The app's project: the scope of the <c>AppTelemetryChanged</c> push that reloads this view.</summary>
    [Parameter] public int ProjectId { get; set; }

    /// <summary>The block the grid reads from the API as it scrolls.</summary>
    private const int PageSize = 25;

    /// <summary>The sort the API applies when the reader sorts no column: most recently seen first.</summary>
    private const string DefaultSort = nameof(AppErrorEventDto.LastSeenAt);

    // The Message column writes the message and, when there is one, the top frame shown under it.
    private static readonly Func<AppErrorEventDto, object?> MessageExport = AppTelemetryMarkdownExport.ErrorMessageExport;

    private AetheusDataGrid<AppErrorEventDto>? _grid;
    private List<AppErrorEventDto> _errors = [];
    private int _total;
    private bool _loading;
    private bool _loadFailed;
    private readonly PushRefresh _pushRefresh = new();
    private int? _loadedAppId;

    /// <summary>
    /// The candidates of the exception-type filter, read from the API: every type the app stored, since
    /// the grid only holds the block on screen.
    /// </summary>
    private List<string> _exceptionTypes = [];

    // Subscribe to the project's AppTelemetryChanged events so newly ingested error groups refresh
    // the grid with its current filters.
    private EntityOperationalFeed? _liveFeed;
    internal EntityOperationalFeed? LiveFeed => _liveFeed;

    protected override async Task OnParametersSetAsync()
    {
        _liveFeed ??= new EntityOperationalFeed(HubFactory);
        _ = _liveFeed.StartAsync(ResourceType.Project, OperationalRealtimeEvents.AppTelemetryChanged, ProjectId,
            () => InvokeAsync(RefreshFromPushAsync));
        if (_loadedAppId == AppId) return;
        var switched = _loadedAppId is not null;
        _loadedAppId = AppId;
        await LoadExceptionTypesAsync();
        // The first load comes from the grid itself; another app on the same instance starts over from
        // its first block, with the filters the reader set.
        if (switched && _grid is not null)
        {
            _errors = [];
            _total = 0;
            await _grid.Reload();
        }
    }

    /// <summary>
    /// Every block the grid asks for goes to the API with the column sort and the header filters, so the
    /// count and the rows describe every error group of the app.
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
            _errors = result.Items;
            _total = result.TotalCount;
            _loadFailed = false;
        }
        catch (HttpRequestException)
        {
            if (AppId != appId) return;
            // A push that fails keeps the groups on screen for the next push. Any other failed call is
            // not "no errors yet": the alert says so.
            if (fromPush) return;
            _errors = [];
            _total = 0;
            _loadFailed = true;
        }
        finally
        {
            if (!fromPush && AppId == appId) _loading = false;
        }
    }

    /// <summary>The exception types offered by the type filter; a failed call keeps the previous list.</summary>
    private async Task LoadExceptionTypesAsync()
    {
        var appId = AppId;
        try
        {
            var types = await Api.Monitoring.GetAppErrorExceptionTypesAsync(appId);
            if (AppId == appId) _exceptionTypes = types;
        }
        catch (HttpRequestException)
        {
            // The grid still works; the list is read again on the next push.
        }
    }

    /// <summary> A push refreshes the grid in place (rows, scroll position and filters kept), and a
    /// new error group reads bold for a few seconds. A new exception type joins the type filter's
    /// list. </summary>
    private async Task RefreshFromPushAsync()
    {
        if (_grid is null) return;
        await LoadExceptionTypesAsync();
        await _pushRefresh.RunAsync(_grid.Refresh);
        StateHasChanged();
    }

    private Task RetryAsync()
    {
        _loadFailed = false;
        return _grid?.Reload() ?? Task.CompletedTask;
    }

    // The whole row opens the error's detail; the dialog only reads the row, so closing it leaves
    // the grid (scroll position, filters, rows) untouched.
    private Task OpenErrorAsync(AppErrorEventDto error) =>
        Dialog.OpenAsync<AppErrorDetailDialog>(L["ErrorDetails"],
            new Dictionary<string, object?> { [nameof(AppErrorDetailDialog.Error)] = error },
            new OmniDialogOptions { Width = "700px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });

    /// <summary> The pages of an export, read from the API with the sort and the column filters of
    /// the grid, without touching the groups it shows (the grid's load replaces them). A failed
    /// page fails the export: no file, <see cref="OnExportFailed"/> says so. </summary>
    private async Task<OmniDataGridResult<AppErrorEventDto>> LoadExportPageAsync(OmniDataGridLoadRequest request)
    {
        var result = await ReadAsync(AppId, request.ToGridLoadArgs());
        return new OmniDataGridResult<AppErrorEventDto>(result.Items, result.TotalCount);
    }

    private Task<PaginatedResult<AppErrorEventDto>> ReadAsync(int appId, GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(defaultPageSize: PageSize);
        var (sortBy, sortDescending) = args.ToSortRequest(DefaultSort, fallbackDescending: true);
        return Api.Monitoring.GetAppErrorsAsync(appId, page: page, pageSize: pageSize,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    private string ExportTitle => AppTelemetryMarkdownExport.Title(L["ErrorsExportTitle"], AppId, AppName);

    private IReadOnlyList<OmniTableExportField> ExportFields =>
        AppTelemetryMarkdownExport.ErrorsFields(AppId, AppName, key => L[key].Value);

    /// <summary><c>aetheus-errors</c>, then OE's generation time and the extension.</summary>
    private string ExportFileName => ExportFileNames.Stem(AppName, $"app-{AppId}", "errors");

    /// <summary>A page that could not be read: no file was produced, the toast says so. Any other
    /// failure goes to the error boundary.</summary>
    private void OnExportFailed(Exception exception)
    {
        if (exception is not HttpRequestException)
            ExceptionDispatchInfo.Capture(exception).Throw();
        Toast.Error("ErrorsExportFailed");
    }

    public async ValueTask DisposeAsync()
    {
        if (_liveFeed is not null)
            await _liveFeed.DisposeAsync();
    }
}
