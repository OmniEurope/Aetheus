// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public abstract class ProjectPagedSectionBase<TItem> : ComponentBase
    where TItem : notnull
{
    [Inject] protected IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] protected ApiClient Api { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    protected int? _loadedProjectId;
    protected int _page = 1;
    protected int _pageSize = 25;
    protected string _sortBy = string.Empty;
    protected bool _sortDescending;
    /// <summary>Each header filter is transmitted as a column filter to the section
    /// endpoint.</summary>
    protected List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    protected AetheusDataGrid<TItem>? _grid;
    protected bool _loading;

    protected abstract string DefaultSortBy { get; }
    protected virtual bool DefaultSortDescending => false;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        _page = 1;
        _sortBy = DefaultSortBy;
        _sortDescending = DefaultSortDescending;
        _columnFilters = [];
        ResetForProject();
        await LoadAsync();
    }

    protected async Task OnLoadDataAsync(GridLoadArgs args)
    {
        (_page, _pageSize) = args.ToPageRequest();
        (_sortBy, _sortDescending) = args.ToSortRequest(DefaultSortBy, DefaultSortDescending);
        _columnFilters = args.ToApiFilters();
        await LoadAsync();
    }

    /// <summary> One page of the section's export, the grid's sort and column filters read from the
    /// request OE sends, through the same endpoint as <see cref="LoadAsync"/> and without touching
    /// the rows the section shows. A page that cannot be read throws, so the export fails and says
    /// so. </summary>
    protected Task<PaginatedResult<TItem>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(DefaultSortBy, DefaultSortDescending);
        return ReadPageAsync(ProjectId, page, pageSize, sortBy, sortDescending, args.ToApiFilters());
    }

    /// <summary>One page of the project's rows from the section's endpoint.</summary>
    protected abstract Task<PaginatedResult<TItem>> ReadPageAsync(
        int projectId, int page, int pageSize, string sortBy, bool sortDescending,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter> filters);

    protected virtual void ResetForProject()
    {
    }

    protected abstract Task LoadAsync();
}
