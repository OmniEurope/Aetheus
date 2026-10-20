// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Logs;

public partial class SystemLogs : ComponentBase, IAsyncDisposable
{
    [Parameter, SupplyParameterFromQuery(Name = "search")]
    public string? Search { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    private List<SystemLogFileDto>? _logFiles;
    private List<string> _fileNames = [];

    // The block the grid last asked for and the size of the whole filtered log. The grid reads the
    // log block by block as it scrolls (remote virtualization); there is no "Load more".
    private AetheusDataGrid<SystemLogEntryDto>? _grid;
    private List<SystemLogEntryDto> _items = [];
    private int _total;
    private bool _loading;
    private bool _loadFailed;
    private readonly PushRefresh _pushRefresh = new();

    // The level filter lists the levels by the same translated names as the page's level drop-down;
    // built once so the column sees the same delegate on every render.
    private Func<string, string>? _levelText;
    private Func<string, string> LevelText => _levelText ??= value =>
        LevelsArray.Contains(value, StringComparer.Ordinal) ? L[value].Value : value;

    private string? _selectedFile;
    private string? _selectedLevel;
    private string? _searchText;
    /// <summary>The window the page opens on: the last three days across every file of every color. The
    /// "from" picker shows it, so the bound is explicit and can be moved back.</summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(72);

    private DateTime? _dateFrom = DateTime.UtcNow - DefaultWindow;
    private DateTime? _dateTo;
    private bool _isDownloading;
    private bool _initialized;
    private string? _appliedSearch;

    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    private AdminEntitySubscription? _liveLogs;
    private readonly TrailingReloadCoalescer _liveReload = new(1000);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _debounceCts;
    private Task? _debounceTask;
    private bool _disposed;
    private const int DebounceDelayMs = 400;

    private static readonly string[] LevelsArray = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
    private List<LogLevelOption> _levelOptions = [];

    private readonly record struct LogLevelOption(string Text, string Value);

    protected override async Task OnInitializedAsync()
    {
        _searchText = Search;
        _appliedSearch = Search;
        _levelOptions = LevelsArray.Select(value => new LogLevelOption(L[value], value)).ToList();
        try
        {
            // The first block of entries comes from the grid itself, when it renders.
            await LoadLogFilesAsync();
            // The back says when the log files changed (at most every 2 s); the grid then refreshes
            // in place, keeping its scroll position and filters.
            _liveLogs = new AdminEntitySubscription(HubFactory);
            await _liveLogs.StartAsync(AdminEntities.SystemLog,
                () => InvokeAsync(() => _liveReload.RequestAsync(RefreshLiveAsync)));
        }
        catch (HttpRequestException) { } // 401 on expired JWT - redirect handled by AuthProvider
        finally
        {
            _initialized = true;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_initialized || string.Equals(Search, _appliedSearch, StringComparison.Ordinal))
            return;

        _appliedSearch = Search;
        _searchText = Search;
        await ReloadEntriesAsync();
    }

    private async Task LoadLogFilesAsync()
    {
        _logFiles = await Api.Security.GetSystemLogFilesAsync();
        _fileNames = _logFiles?.Select(f => f.FileName).ToList() ?? [];
    }

    /// <summary>Exports one page using the selected file, level, search, date range, sort and
    /// column filters.</summary>
    private Task<PaginatedResult<SystemLogEntryDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(defaultPageSize: 50);
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(SystemLogEntryDto.Timestamp), fallbackDescending: true);
        return Api.Security.GetSystemLogEntriesAsync(
            _selectedFile, _selectedLevel, _searchText, _dateFrom, _dateTo, page, pageSize,
            sortBy, sortDescending, args.ToApiFilters());
    }

    private async Task LoadDataAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(defaultPageSize: 50);
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(SystemLogEntryDto.Timestamp), fallbackDescending: true);
        var filters = args.ToApiFilters();
        var fromPush = _pushRefresh.Running;
        if (!fromPush) _loading = true;
        try
        {
            var result = await Api.Security.GetSystemLogEntriesAsync(
                _selectedFile, _selectedLevel, _searchText, _dateFrom, _dateTo, page, pageSize,
                sortBy, sortDescending, filters);
            if (_disposed) return;
            _items = result.Items;
            _total = result.TotalCount;
            _loadFailed = false;
        }
        catch (HttpRequestException)
        {
            // A push that fails keeps the rows on screen for the next push; any other failure is said,
            // never shown as an empty log.
            if (fromPush || _disposed) return;
            _items = [];
            _total = 0;
            _loadFailed = true;
        }
        finally
        {
            if (!fromPush) _loading = false;
        }
    }

    /// <summary>A filter of the bar changed: the grid starts over from its first block.</summary>
    private Task ReloadEntriesAsync() => _grid?.Reload() ?? Task.CompletedTask;

    private Task OnFilterChangedAsync() => ReloadEntriesAsync();

    private async Task OnRefreshAsync()
    {
        await LoadLogFilesAsync();
        await ReloadEntriesAsync();
    }

    private async Task OnSearchTextChanged(string value)
    {
        _searchText = value;
        _debounceCts?.Cancel();
        if (_debounceTask is not null) await _debounceTask;
        _debounceCts?.Dispose();
        _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _debounceTask = RunDebounceAsync(_debounceCts.Token);
    }

    private async Task RunDebounceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceDelayMs, ct);
            await InvokeAsync(ReloadEntriesAsync);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>New log lines refresh the grid in place (rows, scroll position and filters kept);
    /// the lines it did not hold before read bold for a few seconds.</summary>
    private async Task RefreshLiveAsync()
    {
        if (_disposed || _grid is null) return;
        try
        {
            await LoadLogFilesAsync();
        }
        catch (HttpRequestException) { } // 401 on expired JWT during a background tick - redirect handled by AuthProvider
        await _pushRefresh.RunAsync(_grid.Refresh);
        if (!_disposed) StateHasChanged();
    }

    private async Task OnDownloadAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedFile)) return;
        _isDownloading = true;
        StateHasChanged();
        try
        {
            var bytes = await Api.Security.DownloadSystemLogFileAsync(_selectedFile);
            if (bytes is not null)
                await JS.InvokeVoidAsync("downloadFileFromBytes", bytes, _selectedFile, "text/plain");
            else
                Toast.Error("Error", "DownloadFailed");
        }
        finally
        {
            _isDownloading = false;
            StateHasChanged();
        }
    }

    private async Task OnPurgeAsync()
    {
        var confirmed = await Dialog.Confirm(
            L["PurgeConfirmMessage"],
            L["PurgeLogs"],
            new OmniConfirmOptions { Destructive = true, ConfirmIcon = OmniIconName.Broom, OkButtonText = L["Purge"], CancelButtonText = L["GoBack"] });
        if (confirmed != true) return;

        var deleted = await Api.Security.PurgeSystemLogsAsync();
        if (deleted.HasValue)
        {
            Toast.Success("PurgeLogs", "PurgeResult", deleted.Value);
            await OnRefreshAsync();
        }
    }

    private Task OnCopyCorrelationIdAsync(string correlationId) => Clipboard.CopyAsync(correlationId, correlationId);

    private Task OnCopyMessageAsync(string message) => Clipboard.CopyAsync(message, L["MessageCopied"]);

    // RC9T: the whole row opens the message dialog (the explicit "view" button stays too); action
    // buttons stop propagation so a copy click doesn't also pop the dialog.
    private Task OnRowClick(SystemLogEntryDto entry) => ShowMessageDialogAsync(entry);

    private async Task ShowMessageDialogAsync(SystemLogEntryDto entry)
    {
        await Dialog.OpenAsync<LogEntryDialog>(L["Message"],
            new Dictionary<string, object?>
            {
                ["Message"] = entry.Message,
                ["Exception"] = entry.Exception ?? string.Empty
            },
            new OmniDialogOptions { Width = "700px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
    }

    private static OmniTone GetBadgeStyle(string level) => level switch
    {
        "Error" or "Fatal" => OmniTone.Danger,
        "Warning" => OmniTone.Warning,
        "Information" => OmniTone.Accent,
        "Debug" => OmniTone.Neutral,
        _ => OmniTone.Neutral
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();
        _debounceCts?.Cancel();
        if (_liveLogs is not null)
            await _liveLogs.DisposeAsync();

        var tasks = new[] { _debounceTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length > 0) await Task.WhenAll(tasks);

        _debounceCts?.Dispose();
        _lifetimeCts.Dispose();
    }
}
