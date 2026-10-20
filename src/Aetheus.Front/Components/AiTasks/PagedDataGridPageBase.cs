// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.AiTasks;

public abstract class PagedDataGridPageBase<T> : ComponentBase
    where T : notnull
{
    [Inject] protected ApiClient Api { get; set; } = default!;
    [Inject] protected IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] protected OmniDialogService Dialog { get; set; } = default!;
    [Inject] protected BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] protected UiActions Ui { get; set; } = default!;

    protected AetheusDataGrid<T>? _grid;
    protected int _totalCount;
    protected bool _loading;
    protected bool _loadFailed;

    /// <summary>The grid's header filters of the last load, sent with every page request.</summary>
    protected List<GridFilter> _columnFilters = [];
    protected string _sortBy = "Name";
    protected bool _sortDescending;

    /// <summary> For live data. The grid fetches its current page again, with its sort and filters,
    /// the rows staying on screen; a user action keeps <see cref="ReloadPageAsync"/>. </summary>
    protected Task RefreshPageAsync() => _grid?.Refresh() ?? Task.CompletedTask;

    protected async Task ReloadPageAsync()
    {
        if (_grid is not null)
        {
            await _grid.GoToPage(0, forceReload: true).ConfigureAwait(false);
            return;
        }

        await LoadPageAsync(1, 25).ConfigureAwait(false);
    }

    protected Task LoadPageFromArgsAsync(GridLoadArgs args)
    {
        var pageSize = args.Top ?? 25;
        var page = (args.Skip ?? 0) / pageSize + 1;
        _columnFilters = args.ToApiFilters();
        (_sortBy, _sortDescending) = args.ToSortRequest(DefaultSortField);
        return LoadPageAsync(page, pageSize);
    }

    /// <summary>The column a page sorts on until the user sorts another one.</summary>
    protected virtual string DefaultSortField => "Name";

    protected async Task<PaginatedResult<T>> LoadPageResultAsync(
        Func<Task<PaginatedResult<T>>> loadAsync)
    {
        _loading = true;
        try
        {
            var result = await loadAsync().ConfigureAwait(false);
            _totalCount = result.TotalCount;
            _loadFailed = false;
            return result;
        }
        catch (HttpRequestException)
        {
            // An honest failure the page shows with a Retry button.
            _loadFailed = true;
            return new PaginatedResult<T>();
        }
        finally
        {
            _loading = false;
        }
    }

    protected abstract Task LoadPageAsync(int page, int pageSize);
}
