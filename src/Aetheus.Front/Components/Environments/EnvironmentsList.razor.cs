// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Environments;

/// <summary>
/// 0-a: shared Environments list. Environments aren't server-scoped, so the component supports the
/// global (unfiltered) and project scopes - both server-paginated via <c>GetEnvironmentsAsync</c>
/// (the endpoint supports a <c>projectId</c> filter). Self-loading - no dependency on the detail
/// loaders; the <c>entities</c> hub keeps the grid live.
/// </summary>
public partial class EnvironmentsList : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    /// <summary>Project filter (project detail scope). When null the list is global (unfiltered).</summary>
    [Parameter] public int? ProjectId { get; set; }

    private bool IsProjectScope => ProjectId.HasValue;

    private List<EnvironmentDto> _environments = [];
    private int _totalCount;
    private bool _loading;
    private bool _canWrite;
    // The column header filters, each a real column filter of the endpoint.
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private Func<string, string>? _typeText;
    private Func<string, string>? _yesNoText;
    private Func<string, string> TypeText => _typeText ??= GridFilterText.ForEnum<EnvironmentType>(L);
    private Func<string, string> YesNoText => _yesNoText ??= GridFilterText.YesNo(L);
    private HubConnection? _hubConnection;

    protected override async Task OnInitializedAsync()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        // Stale-while-revalidate: pre-seed from the cached default view so the first paint is instant;
        // OnLoadData then revalidates in the background.
        Cache.Seed<PaginatedResult<EnvironmentDto>>(CacheKey(1, 20), ApplyEnvironments);
        await StartHubAsync();
    }

    // The sort belongs in the key: without it two orders share one entry and the second is served the
    // first one's rows, which looks exactly like a sort that does nothing.
    private string CacheKey(int page, int pageSize, string? sortBy = null, bool sortDescending = false) =>
        $"environments:{ProjectId}:{page}:{pageSize}:{GridColumnFilters.CacheText(_columnFilters)}:{sortBy}:{sortDescending}";

    private void ApplyEnvironments(PaginatedResult<EnvironmentDto> result)
    {
        _environments = result.Items;
        _totalCount = result.TotalCount;
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.Environment);

    /// <summary>One page of the export, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<EnvironmentDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(EnvironmentDto.Name));
        return Api.Servers.GetEnvironmentsAsync(page: page, pageSize: pageSize, projectId: ProjectId,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    private async Task OnLoadData(GridLoadArgs args)
    {
        _columnFilters = args.ToApiFilters();
        var filters = _columnFilters;
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(EnvironmentDto.Name));
        await Cache.RevalidateAsync(
            CacheKey(page, pageSize, sortBy, sortDescending),
            () => Api.Servers.GetEnvironmentsAsync(page: page, pageSize: pageSize, projectId: ProjectId,
                sortBy: sortBy, sortDescending: sortDescending, filters: filters),
            ApplyEnvironments,
            loading => _loading = loading,
            () => InvokeAsync(StateHasChanged));
    }

    private AetheusDataGrid<EnvironmentDto>? _grid;
    // Live changes refresh quietly (rows, page, scroll and filters kept).
    private Task RefreshGrid() => _grid?.Refresh() ?? Task.CompletedTask;


    private void NewEnvironment() => Nav.NavigateTo(IsProjectScope
        ? $"/environments/new?projectId={ProjectId}"
        : "/environments/new");

    private async Task DuplicateEnvironment(EnvironmentDto env)
    {
        var result = await Api.Servers.DuplicateEnvironmentAsync(env.Id, new DuplicateEnvironmentRequest { TargetProjectId = env.ProjectId });
        if (result is not null)
        {
            Toast.Success("Created", "EnvironmentDuplicated");
            Nav.NavigateTo($"/environments/{result.Id}");
        }
    }

    private async Task StartHubAsync()
    {
        try
        {
            _hubConnection = HubFactory.Create("entities");
            _hubConnection.On<ResourceType, int, string>("EntityChanged", (type, _, _) =>
            {
                if (type == ResourceType.Environment)
                    return InvokeAsync(RefreshGrid);
                return Task.CompletedTask;
            });
            // Group membership is per-connection and lost on auto-reconnect - re-join + refresh.
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Environment);
                await RefreshGrid();
            }));
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Environment);
        }
        catch { /* Hub unavailable - degrade to static */ }
    }

    public ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        return DisposeHubAsync();
    }

    private async ValueTask DisposeHubAsync()
    {
        if (_hubConnection is null) return;
        await _hubConnection.LeaveEntityUpdatesAndDisposeAsync(ResourceType.Environment);
        _hubConnection = null;
    }

    /// <summary>The export writes the count the badge shows.</summary>
    private static readonly Func<EnvironmentDto, object?> ServersExport = environment => environment.Servers.Count;
}
