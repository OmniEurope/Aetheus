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

    /// <summary>
    /// The page is loading this grid's data itself. Handed to OE's <c>Busy</c>, so the grid shows the same
    /// loading bar as for its own requests (recette R-432).
    /// </summary>
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
    [Parameter] public bool FullHeight { get; set; }

    /// <summary>Recette R-226 / R-227: the property telling rows apart, so a live refresh can mark the
    /// rows it brought in. Every Aetheus DTO listed in a grid carries an <c>Id</c>.</summary>
    [Parameter] public string? KeyProperty { get; set; } = "Id";

    /// <summary>Recette R-227: how long a row brought in by <see cref="Refresh"/> reads bold.</summary>
    [Parameter] public TimeSpan? NewRowHighlight { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Recette R-016 / R-024: each column filters from a menu in its header, not from a second
    /// header row. On by default for every list; a grid can still turn it off.</summary>
    [Parameter] public bool ShowHeaderFilterMenu { get; set; } = true;

    /// <summary>Recette R-016: alternating row shades on every list by default.</summary>
    [Parameter] public bool AllowAlternatingRows { get; set; } = true;

    // Recette R2-011: the export bar of OE's grid, declared here so a page sets it by name and type instead
    // of through the unmatched attributes. Its buttons are OE's own, each with the file icon of its format
    // (OE 1.4.0). Recette R2-124: a list that exports writes all its rows, titled by the page; only the
    // tables listed in GridExportCoverageAuditTests show the bar (Markdown, second decision of 2026-10-05).
    // OE 1.6.0: the bar sits under the rows, inside the table's frame; Aetheus puts its buttons at its end
    // (user decision of 2026-10-06, OE's default stays the start).

    /// <summary>
    /// Recette R2-124 (decision of 2026-10-05): true shows the bar under the rows with the Markdown button,
    /// for a prompt; CSV, Excel and PDF are always off. Only the tables worth handing to an AI ask for it,
    /// listed with their reason in <c>GridExportCoverageAuditTests</c>. Off by default: no bar.
    /// </summary>
    [Parameter] public bool ShowFooterBar { get; set; }

    /// <summary>
    /// False is the opt-out of a table that must never be exported (a secret value on screen, say): no
    /// format, so no bar even when <see cref="ShowFooterBar"/> asks for one, and no export reader needed.
    /// <c>GridExportCoverageAuditTests</c> holds it to its approved list.
    /// </summary>
    [Parameter] public bool ExportMarkdown { get; set; } = true;

    /// <summary>
    /// The variant of each format's button. By default <see cref="AetheusGrid.ExportVariants"/>: the
    /// Markdown export blue on every export bar of Aetheus (recette R2-009, the user's exception to STD-BTN),
    /// the other formats Ghost.
    /// </summary>
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

    /// <summary>
    /// The file name without extension and without date (see <see cref="ExportFileNames.Stem"/>); OE appends
    /// its generation time and the extension, <c>aetheus-logs-2026-10-01-0840.md</c> (recette R2-036). Null
    /// writes the page's breadcrumb past its module, then <see cref="ExportName"/> (<see cref="GridExportContext.FileName"/>).
    /// </summary>
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
    // The pager of a remote paged grid counts the page's own total, as before OE 1.2.0; a virtualized grid
    // sizes its scroll from what Load returns, and a local grid counts its rows.
    private int? EffectiveCount => UsesRemoteLoad && !EffectiveVirtualize ? Count : null;
    // The remote loader, paged or virtualized. A method group converted at each render is a new delegate
    // instance, but OmniDataGrid (OE 1.2.0) compares its Load by delegate equality (same method, same
    // target), so a render does not reset what it had fetched (the request loop the cached field used to
    // prevent on the 14 remote-virtualized pages). OE sends the first request once the columns have
    // registered, carrying their default filters and sorts (OE 35969ce).
    private Func<OmniDataGridLoadRequest, Task<OmniDataGridResult<TItem>>>? EffectiveLoad =>
        UsesRemoteLoad ? LoadRemoteAsync : null;
    private bool EffectiveBusy => IsLoading;

    private EventCallback<OmniDataGridRowMouseEventArgs<TItem>> EffectiveRowClick => RowClick.HasDelegate
        ? EventCallback.Factory.Create<OmniDataGridRowMouseEventArgs<TItem>>(this, args => RowClick.InvokeAsync(args.Item))
        : default;

    // OE 1.2.0 takes the row key as a selector (KeyOf); the wrapper keeps naming the property, read the way
    // OE's retired KeyProperty read it: a public instance property, no key when the type has none.
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

        // A paged grid showed the page's own rows before OE 1.2.0 (the page fed Items and Count). It now shows
        // what its own last Load returned, so a load the page ran by itself (a first load, a change of project,
        // a failure) that ends while the grid is not loading, signalled through IsLoading or by replacing the
        // rows the grid last received, hands the grid the page's rows. A virtualized grid keeps its own window
        // and catches up on a changed total only (below).
        if (UsesRemoteLoad && !EffectiveVirtualize && _remoteLoadsInProgress == 0 && ((_pageWasLoading && !IsLoading) || PageRowsReplaced()))
        {
            _pageLoadSettled = true;
        }
        _pageWasLoading = IsLoading;
    }

    // Recette R2-124: the shared export of a list. The title and the header lines say where the rows come
    // from unless the page wrote its own; a LoadData grid reads its export through ExportRead. The opt-out
    // (ExportMarkdown="false", a table that must never export) wins over ShowFooterBar.
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

    // Recette R-181 / R-185: a page that loads its data itself (first load, cache revalidation) while
    // the remote virtualized grid pulls through LoadData races it: the host drops the grid's request as stale
    // and the grid keeps what it got then, typically an empty list under a "7056 in total" header.
    // When the total the host now holds differs from the one the grid last received, the grid pulls
    // again, once; an equal total leaves it alone, so this cannot loop.
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
            _pageLoadSettled = false;
            _servingHeldRows = true;
            try
            {
                await Grid.ReloadAsync();
            }
            finally
            {
                _servingHeldRows = false;
            }
            return;
        }
        if (!UsesRemoteLoad || !EffectiveVirtualize || _catchingUp || _remoteLoadsInProgress > 0 || _lastRemoteCount is not { } seen || Grid is null) return;
        if (VirtualData!().TotalCount == seen) return;
        _catchingUp = true;
        try
        {
            await Grid.ReloadAsync();
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

    /// <summary>
    /// Recette R-226: for live data (a SignalR push, a feed event). The rows stay on screen while the
    /// page fetches again, with no loader, the scroll position and the page kept; the new rows replace
    /// them when they arrive and the ones that were not there read bold for <see cref="NewRowHighlight"/>.
    /// A sort, a filter or a user action still uses <see cref="Reload"/>.
    /// </summary>
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
