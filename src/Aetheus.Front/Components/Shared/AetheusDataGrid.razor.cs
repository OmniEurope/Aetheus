// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Aetheus.Front.Components.Shared;

public partial class AetheusDataGrid<TItem> where TItem : notnull
{
    [Inject] private ILogger<AetheusDataGrid<TItem>> Logger { get; set; } = default!;
    [Inject] private GridExportContext ExportContext { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    [Parameter] public IEnumerable<TItem>? Data { get; set; }
    [Parameter] public int Count { get; set; }

    /// <summary>Expose page-owned loading through OE's Busy state so manual and grid-owned requests
    /// use the same progress indicator.</summary>
    [Parameter] public bool IsLoading { get; set; }
    [Parameter] public EventCallback<GridLoadArgs> LoadData { get; set; }

    /// <summary>
    /// The rows and the total the page holds once its <see cref="LoadData"/> has run, read at once when the
    /// load returns (a parameter would only arrive with the page's next render). Required with
    /// <see cref="LoadData"/>, paged or virtualized.
    /// </summary>
    [Parameter] public Func<OmniDataGridResult<TItem>>? VirtualData { get; set; }
    [Parameter] public RenderFragment? Columns { get; set; }
    [Parameter] public bool AllowSorting { get; set; } = true;
    [Parameter] public bool AllowFiltering { get; set; } = true;
    [Parameter] public bool AllowColumnResize { get; set; } = true;
    [Parameter] public OmniDataGridFilterMode FilterMode { get; set; } = OmniDataGridFilterMode.Simple;
    [Parameter] public int PageSize { get; set; } = 20;
    [Parameter] public bool Virtualize { get; set; } = AetheusGrid.Virtualization;
    [Parameter] public int VirtualizationOverscanCount { get; set; } = 3;
    [Parameter] public OmniDensity Density { get; set; } = OmniDensity.Compact;
    [Parameter] public string? EmptyText { get; set; }
    [Parameter] public RenderFragment? EmptyTemplate { get; set; }
    [Parameter] public bool AutoLoad { get; set; } = true;
    [Parameter] public EventCallback<TItem> RowClick { get; set; }
    [Parameter] public EventCallback<TItem> RowUpdate { get; set; }
    [Parameter] public Action<OmniDataGridRowRenderArgs<TItem>>? RowRender { get; set; }
    [Parameter] public OmniDataGridRowMode EditMode { get; set; } = OmniDataGridRowMode.Single;
    [Parameter] public bool AllowAltering { get; set; }

    /// <summary>What puts a row in edit mode (OE <c>OmniDataGrid.EditTrigger</c>). <c>Cell</c>: a single click
    /// or key anywhere in a cell whose column has an <c>EditTemplate</c> edits the row, the focus landing in that
    /// cell (recette R-75).</summary>
    [Parameter] public OmniDataGridEditTrigger EditTrigger { get; set; } = OmniDataGridEditTrigger.Action;

    /// <summary>Raised when a row enters edit mode, whichever way (a cell, <see cref="EditRow"/>), before its
    /// editors render: the page loads its edit buffers here.</summary>
    [Parameter] public EventCallback<TItem> RowEdit { get; set; }
    [Parameter] public bool FullHeight { get; set; }

    /// <summary>Identify rows by their DTO Id so live refresh can highlight newly received
    /// rows.</summary>
    [Parameter] public string? KeyProperty { get; set; } = "Id";

    /// <summary>How long a row brought in by <see cref="Refresh"/> reads bold.</summary>
    [Parameter] public TimeSpan? NewRowHighlight { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Each column filters from a menu in its header, not from a second header row. On by
    /// default for every list; a grid can still turn it off.</summary>
    [Parameter] public bool ShowHeaderFilterMenu { get; set; } = true;

    /// <summary>Alternating row shades on every list by default.</summary>
    [Parameter] public bool AllowAlternatingRows { get; set; } = true;

    /// <summary>OE row selection (a checkbox column, the selected rows filled). None by default: only a
    /// grid whose rows are chosen and stay chosen asks for it (the release picker, recette R-58).</summary>
    [Parameter] public OmniDataGridSelectionMode SelectionMode { get; set; }

    /// <summary>A click on a row also selects it, when <see cref="SelectionMode"/> allows one.</summary>
    [Parameter] public bool AllowRowSelectOnRowClick { get; set; }

    /// <summary>The selected rows, matched to the rows on screen through the key; a new list from the host
    /// replaces the grid's own selection.</summary>
    [Parameter] public IReadOnlyList<TItem> Value { get; set; } = [];

    /// <summary>Raised with the selected rows each time the selection changes.</summary>
    [Parameter] public EventCallback<IReadOnlyList<TItem>> ValueChanged { get; set; }

    // Expose typed export parameters so pages do not configure the grid through unmatched
    // attributes; eligible tables export all filtered rows with page context and place Markdown
    // actions at the footer's end.

    /// <summary>Enable the Markdown footer only for tables approved by GridExportCoverageAuditTests
    /// so export remains intentional; CSV, Excel and PDF stay disabled.</summary>
    [Parameter] public bool ShowFooterBar { get; set; }

    /// <summary>
    /// False is the opt-out of a table that must never be exported (a secret value on screen, say): no
    /// format, so no bar even when <see cref="ShowFooterBar"/> asks for one, and no export reader needed.
    /// <c>GridExportCoverageAuditTests</c> holds it to its approved list.
    /// </summary>
    [Parameter] public bool ExportMarkdown { get; set; } = true;

    /// <summary>Use AetheusGrid.ExportVariants so Markdown is the export area's blue action and
    /// other formats retain Ghost styling, under the product's STD-BTN exception.</summary>
    [Parameter] public IReadOnlyDictionary<OmniTableExportFormat, OmniButtonVariant> ExportVariants { get; set; } = AetheusGrid.ExportVariants;

    /// <summary>
    /// The title of the exported document. Null, the default, writes the page's breadcrumb followed by
    /// <see cref="ExportName"/> (<see cref="GridExportContext.Title"/>).
    /// </summary>
    [Parameter] public string? ExportTitle { get; set; }

    /// <summary>
    /// What the table holds, appended to the breadcrumb in the default title: required where a page
    /// holds several tables, so their files do not share one title and one name.
    /// </summary>
    [Parameter] public string? ExportName { get; set; }

    /// <summary>
    /// The header lines of the exported document (application, period); OE adds the active column filters.
    /// Empty, the default, writes the application and the address of the page (<see cref="GridExportContext.Fields"/>).
    /// </summary>
    [Parameter] public IReadOnlyList<OmniTableExportField> ExportFields { get; set; } = [];

    /// <summary>
    /// Reads one page of the rows an export writes, from the API, with the sort and column filters of the
    /// grid: the page's own reading, without the rows, the cache or the error state its
    /// <see cref="LoadData"/> keeps. OE asks 200 rows at a time until every row is read. A failed read
    /// throws, so the export fails and says so instead of writing a partial file. The simpler way to
    /// give a <see cref="LoadData"/> grid its export; <see cref="ExportLoad"/> wins when both are set.
    /// </summary>
    [Parameter] public Func<GridLoadArgs, Task<PaginatedResult<TItem>>>? ExportRead { get; set; }

    /// <summary> The file name without extension and without date (see <see
    /// cref="ExportFileNames.Stem"/>); OE appends its generation time and the extension,
    /// <c>aetheus-logs-2026-10-01-0840.md</c>. Null writes the page's breadcrumb past its module,
    /// then <see cref="ExportName"/> (<see cref="GridExportContext.FileName"/>). </summary>
    [Parameter] public string? ExportFileName { get; set; }

    /// <summary>
    /// Reads the rows of an export page after page. Required on a grid fed by <see cref="LoadData"/>: that
    /// load replaces the rows the page holds, which an export must not disturb.
    /// </summary>
    [Parameter] public Func<OmniDataGridLoadRequest, Task<OmniDataGridResult<TItem>>>? ExportLoad { get; set; }

    /// <summary>
    /// Raised when an export fails; no file is produced. Without a handler, a page that could not be read
    /// raises the <c>GridExportFailed</c> toast and any other failure goes to the error boundary.
    /// </summary>
    [Parameter] public EventCallback<Exception> OnExportError { get; set; }
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AdditionalAttributes { get; set; }

    public OmniDataGrid<TItem>? Grid { get; private set; }
    public int CurrentPage => Math.Max(0, _currentPage - 1);

    private bool _loadFailed;
    private int _currentPage = 1;

    private IReadOnlyList<TItem> GridItems => Data as IReadOnlyList<TItem> ?? Data?.ToArray() ?? [];
    private bool UsesRemoteLoad => LoadData.HasDelegate;
    private bool EffectiveVirtualize => Virtualize;
    private IReadOnlyList<TItem> EffectiveItems => UsesRemoteLoad ? [] : GridItems;
    // Use the page's total for remote pagination, the loader's result for virtualized scrolling and
    // the row count for local grids so each mode measures its actual data source.
    private int? EffectiveCount => UsesRemoteLoad && !EffectiveVirtualize ? Count : null;
    // OE compares Load delegates by method and target, so rerendering does not reset fetched data;
    // its first request follows column registration and includes default filters and sorts.
    private Func<OmniDataGridLoadRequest, Task<OmniDataGridResult<TItem>>>? EffectiveLoad =>
        UsesRemoteLoad ? LoadRemoteAsync : null;
    private bool EffectiveBusy => IsLoading;

    private EventCallback<OmniDataGridRowMouseEventArgs<TItem>> EffectiveRowClick => RowClick.HasDelegate
        ? EventCallback.Factory.Create<OmniDataGridRowMouseEventArgs<TItem>>(this, args => RowClick.InvokeAsync(args.Item))
        : default;

    // Resolve KeyOf through the named public instance property so the wrapper retains a
    // property-based contract; types without that property have no key.
    private string? _keyOfProperty;
    private Func<TItem, object>? _keyOf;
    private Func<TItem, object>? EffectiveKeyOf
    {
        get
        {
            if (!string.Equals(_keyOfProperty, KeyProperty, StringComparison.Ordinal))
            {
                _keyOfProperty = KeyProperty;
                var property = string.IsNullOrWhiteSpace(KeyProperty)
                    ? null
                    : typeof(TItem).GetProperty(KeyProperty, BindingFlags.Instance | BindingFlags.Public);
                _keyOf = property is null ? null : item => property.GetValue(item)!;
            }
            return _keyOf;
        }
    }

    private static readonly RenderFragment PendingEmptyContent = _ => { };
    private RenderFragment? VisibleEmptyTemplate => ShowEmptyContent ? EmptyTemplate : PendingEmptyContent;

    private string GridCssClass
    {
        get
        {
            var passed = AdditionalAttributes is not null
                && AdditionalAttributes.TryGetValue("class", out var cssClass) ? cssClass?.ToString() : null;
            var classes = new List<string>();
            if (!string.IsNullOrWhiteSpace(passed)) classes.Add(passed);
            classes.Add("aetheus-grid");
            if (RowClick.HasDelegate) classes.Add("aetheus-clickable-rows");
            if (FullHeight) classes.Add("aetheus-grid-fullheight");
            if (EffectiveVirtualize) classes.Add("aetheus-grid-virtualized");
            return string.Join(" ", classes);
        }
    }

    private Dictionary<string, object>? OtherAttributes =>
        AdditionalAttributes?.Where(attribute => attribute.Key != "class")
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

    private bool _firstLoadSettled;
    internal bool ShowEmptyContent => !IsLoading && (!LoadData.HasDelegate || _firstLoadSettled);
    private string EffectiveEmptyText => ShowEmptyContent ? EmptyText ?? L["NoRecords"].Value : string.Empty;

    protected override void OnParametersSet()
    {
        if (LoadData.HasDelegate && VirtualData is null)
        {
            throw new InvalidOperationException(
                $"AetheusDataGrid<{typeof(TItem).Name}>: LoadData needs VirtualData, the rows and total the page holds after a load.");
        }

        if (LoadData.HasDelegate && Exports && EffectiveExportLoad is null)
        {
            throw new InvalidOperationException(
                $"AetheusDataGrid<{typeof(TItem).Name}>: an export bar on a LoadData grid needs ExportRead (or ExportLoad); "
                + "LoadData replaces the page's rows. A table that must not be exported writes ExportMarkdown=\"false\".");
        }

        // When a page-owned load completes outside the grid's request, hand its rows to the paged
        // grid so both views stay synchronized; virtualized grids retain their own window and
        // reconcile only changed totals.
        if (UsesRemoteLoad && !EffectiveVirtualize && _remoteLoadsInProgress == 0 && ((_pageWasLoading && !IsLoading) || PageRowsReplaced()))
        {
            _pageLoadSettled = true;
        }
        _pageWasLoading = IsLoading;
    }

    // Supply page context and ExportRead for complete exports without disturbing displayed rows;
    // ExportMarkdown=false always overrides ShowFooterBar for tables that must never export.
    private bool Exports => ShowFooterBar && ExportMarkdown;

    private string EffectiveExportTitle => ExportTitle ?? ExportContext.Title(ExportName);

    private string? EffectiveExportFileName => ExportFileName ?? ExportContext.FileName(ExportName);

    private IReadOnlyList<OmniTableExportField> EffectiveExportFields =>
        ExportFields.Count > 0 ? ExportFields : ExportContext.Fields();

    private Func<OmniDataGridLoadRequest, Task<OmniDataGridResult<TItem>>>? EffectiveExportLoad =>
        ExportLoad ?? (ExportRead is null ? null : ReadExportPageAsync);

    private EventCallback<Exception> EffectiveExportError => OnExportError.HasDelegate
        ? OnExportError
        : EventCallback.Factory.Create<Exception>(this, ExportFailed);

    /// <summary>One page of an export, through the page's reader; the rows on screen are left alone.</summary>
    private async Task<OmniDataGridResult<TItem>> ReadExportPageAsync(OmniDataGridLoadRequest request)
    {
        var page = await ExportRead!(request.ToGridLoadArgs());
        return new OmniDataGridResult<TItem>(page.Items, page.TotalCount);
    }

    /// <summary>A page that could not be read: no file was produced, the toast says so. Any other failure
    /// is a defect and goes to the error boundary, as the page's own handlers do.</summary>
    private void ExportFailed(Exception exception)
    {
        if (exception is not HttpRequestException)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        Toast.Error("GridExportFailed");
    }

    private bool PageRowsReplaced()
    {
        if (_lastServedRows is null) return false;
        var held = VirtualData!().Items;
        return !ReferenceEquals(held, _lastServedRows) && (held.Count > 0 || _lastServedRows.Count > 0);
    }

    private async Task<OmniDataGridResult<TItem>> LoadRemoteAsync(OmniDataGridLoadRequest request)
    {
        _remoteLoadsInProgress++;
        try
        {
            _loadFailed = false;
            if (_servingHeldRows)
            {
                // The page has just loaded these rows itself; asking it again would fetch them twice.
                _servingHeldRows = false;
            }
            else
            {
                await LoadData.InvokeAsync(request.ToGridLoadArgs());
            }
            var result = VirtualData!();
            _lastRemoteCount = result.TotalCount;
            _lastServedRows = result.Items;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Grid LoadData failed for {ItemType}; showing the error state.", typeof(TItem).Name);
            _loadFailed = true;
            await InvokeAsync(StateHasChanged);
            return new OmniDataGridResult<TItem>([], 0);
        }
        finally
        {
            var firstSettle = !_firstLoadSettled;
            _firstLoadSettled = true;
            _remoteLoadsInProgress--;
            // Load is a plain delegate, not an event of the grid: nothing renders the wrapper after it, so
            // the empty state it hands the grid would stay the pending one until some other render.
            if (firstSettle) _ = InvokeAsync(StateHasChanged);
        }
    }

    // Reload once when the host's total differs from the virtualized grid's last result so
    // concurrent page-owned loading cannot strand a stale window; equal totals prevent a reload
    // loop.
    private int? _lastRemoteCount;
    private bool _catchingUp;
    private int _remoteLoadsInProgress;
    private bool _pageWasLoading;
    private bool _pageLoadSettled;
    private bool _servingHeldRows;
    private IReadOnlyList<TItem>? _lastServedRows;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_pageLoadSettled && UsesRemoteLoad && _remoteLoadsInProgress == 0 && Grid is not null)
        {
            await ServeHeldRowsAsync(Grid);
            return;
        }
        await CatchUpVirtualCountAsync();
    }

    // The page load settled: reload once more so the grid serves the rows it held back.
    private async Task ServeHeldRowsAsync(OmniDataGrid<TItem> grid)
    {
        _pageLoadSettled = false;
        _servingHeldRows = true;
        try
        {
            await grid.ReloadAsync();
        }
        finally
        {
            _servingHeldRows = false;
        }
    }

    // The total of the last remote load, when a catch-up may run now: a virtualized remote grid, built,
    // with no load in flight and no catch-up already running.
    private int? CatchUpBaseline() =>
        UsesRemoteLoad && EffectiveVirtualize && !_catchingUp && _remoteLoadsInProgress == 0 && Grid is not null
            ? _lastRemoteCount
            : null;

    // A virtualized remote grid whose total changed since its last load reloads once to catch up.
    private async Task CatchUpVirtualCountAsync()
    {
        if (CatchUpBaseline() is not { } seen || VirtualData!().TotalCount == seen) return;
        _catchingUp = true;
        try
        {
            await Grid!.ReloadAsync();
        }
        finally
        {
            _catchingUp = false;
        }
    }

    private Task OnPageChangedAsync(int page)
    {
        _currentPage = page;
        return Task.CompletedTask;
    }

    private async Task RetryAsync()
    {
        _loadFailed = false;
        await Reload();
    }

    public Task Reload() => Grid?.ReloadAsync() ?? Task.CompletedTask;

    /// <summary>Refresh live data without hiding rows or resetting scroll and page state; newly
    /// received rows use NewRowHighlight, while sorting, filtering and explicit actions use
    /// Reload.</summary>
    public Task Refresh() => Grid?.RefreshAsync() ?? Task.CompletedTask;

    public void EditRow(TItem item) => _ = Grid?.EditRowAsync(item);
    public void UpdateRow(TItem item) => _ = Grid?.UpdateRowAsync(item);
    public void CancelEditRow(TItem item) => _ = Grid?.CancelEditAsync(item);

    public async Task GoToPage(int page, bool forceReload = false)
    {
        var target = Math.Max(0, page) + 1;
        var changed = target != _currentPage;
        _currentPage = target;
        if ((changed || forceReload) && Grid is not null)
        {
            await Grid.ReloadAsync();
        }
    }
}
