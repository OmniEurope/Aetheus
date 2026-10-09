// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Tasks;

/// <summary>
/// Reusable task list shared by the global <c>/tasks</c> page and the per-server tasks section.
/// When <see cref="ServerId"/> is set the grid is scoped to that one server (the redundant Server
/// column is also hidden); otherwise it shows every task the caller can see. Identical features in
/// both hosts: column header filters, server-side pagination, status + offline badges, log viewer,
/// and live SignalR refresh.
/// </summary>
public partial class TaskListView : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    /// <summary>When set, scope the list to a single server (per-server tasks view).</summary>
    [Parameter] public int? ServerId { get; set; }
    [Parameter] public string? ServerName { get; set; }

    private AetheusDataGrid<ServerTaskDto>? _grid;
    private List<ServerTaskDto> _tasks = [];
    private int _totalCount;
    private bool _loading;
    private bool _loadFailed;
    private HubConnection? _hubConnection;
    private bool _hasLoadedServerId;
    private int? _loadedServerId;

    // Recette R-212: the column header filters, sent to the API, and the server names the Server column offers.
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private TaskFilterValuesDto _filterValues = new();
    private Func<string, string>? _executorText;
    private Func<string, string>? _statusText;
    private Func<string, string> ExecutorText => _executorText ??= GridFilterText.ForEnum<ExecutorType>(L);
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<TaskExecutionStatus>(L);

    /// <summary>The server names across the tasks in scope (this server's only, in the per-server view).</summary>
    private async Task LoadFilterValuesAsync()
    {
        try
        {
            _filterValues = await Api.Pipelines.GetTaskFilterValuesAsync(ServerId);
        }
        catch (HttpRequestException)
        {
            _filterValues = new TaskFilterValuesDto();
        }
    }

    protected override Task OnInitializedAsync() => LoadFilterValuesAsync();

    protected override void OnInitialized()
    {
        // Stale-while-revalidate: pre-seed from the cached default view so revisiting the tasks list
        // paints instantly; OnLoadData then revalidates in the background.
        Cache.Seed<PaginatedResult<ServerTaskDto>>(CacheKey(1, 20), ApplyTasks);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_hasLoadedServerId)
        {
            _hasLoadedServerId = true;
            _loadedServerId = ServerId;
            return;
        }
        if (_loadedServerId == ServerId) return;

        _loadedServerId = ServerId;
        _tasks = [];
        _totalCount = 0;
        await LoadFilterValuesAsync();
        Cache.Seed<PaginatedResult<ServerTaskDto>>(CacheKey(1, 20), ApplyTasks);
        if (_grid is not null)
        {
            await _grid.GoToPage(0);
            await _grid.Reload();
        }
    }

    // The sort is part of the key: two orders sharing one entry means the second is served the first
    // one's rows, which is indistinguishable from a sort that does nothing.
    private string CacheKey(int page, int pageSize, string? sortBy = null, bool sortDescending = true) =>
        $"tasks:{ServerId}:{page}:{pageSize}:{GridColumnFilters.CacheText(_columnFilters)}:{sortBy}:{sortDescending}";

    private void ApplyTasks(PaginatedResult<ServerTaskDto> result)
    {
        _tasks = result.Items;
        _totalCount = result.TotalCount;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            // Task lifecycle events ride the servers hub (AllServers group). Any of them refreshes
            // the grid so a new/started/finished task surfaces without a manual reload. Recette R-226:
            // a quiet refresh, the rows, page, scroll and filters stay put and a new row shows in bold.
            _hubConnection = HubFactory.Create("servers");
            _hubConnection.On<object>("TaskQueued", _ => InvokeAsync(InvalidateAndRefreshGridAsync));
            _hubConnection.On<object>("TaskStarted", _ => InvokeAsync(InvalidateAndRefreshGridAsync));
            _hubConnection.On<object>("TaskCompleted", _ => InvokeAsync(InvalidateAndRefreshGridAsync));
            // Group membership is per-connection and lost on auto-reconnect - re-join + refresh.
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinAllServers");
                await InvalidateAndRefreshGridAsync();
            }));
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinAllServers");
        }
        catch { /* SignalR best-effort: fall back to manual refresh. */ }
    }

    private async Task ReloadGridAsync()
    {
        if (_grid is not null) await _grid.Reload();
    }

    private async Task InvalidateAndRefreshGridAsync()
    {
        Cache.InvalidatePrefix("tasks:");
        if (_grid is not null) await _grid.Refresh();
    }

    /// <summary>Recette R2-124: one page of the export, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<ServerTaskDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(ServerTaskDto.CreatedAt), fallbackDescending: true);
        return Api.Pipelines.GetTasksAsync(page, pageSize, ServerId, sortBy, sortDescending, args.ToApiFilters());
    }

    private async Task OnLoadData(GridLoadArgs args)
    {
        _loadFailed = false;
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(ServerTaskDto.CreatedAt), fallbackDescending: true);
        var serverId = ServerId;
        _columnFilters = args.ToApiFilters();
        var filters = _columnFilters;
        await Cache.RevalidateAsync(
            CacheKey(page, pageSize, sortBy, sortDescending),
            () => Api.Pipelines.GetTasksAsync(page, pageSize, serverId, sortBy, sortDescending, filters),
            result => { if (ServerId == serverId) ApplyTasks(result); },
            loading => { if (ServerId == serverId) _loading = loading; },
            () => InvokeAsync(StateHasChanged),
            _ => { if (ServerId == serverId) _loadFailed = true; });
    }

    internal void OpenTask(ServerTaskDto task) => Nav.NavigateTo($"/tasks/{task.Id}");

    // A Pending/Assigned task whose server agent is offline cannot progress on its own - surface that
    // instead of an opaque "Pending". The timeout watchdog force-fails it after 30 min.
    internal static bool IsStalledOnOfflineAgent(ServerTaskDto task) =>
        task.ServerStatus == ServerStatus.Offline
        && task.Status is TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned;

    internal string GetSourceServerName(ServerTaskDto task) =>
        !string.IsNullOrWhiteSpace(task.ServerName)
            ? task.ServerName
            : !string.IsNullOrWhiteSpace(ServerName)
                ? ServerName
                : $"#{task.ServerId}";

    internal static OmniTone GetTaskBadge(TaskExecutionStatus status) => status switch
    {
        TaskExecutionStatus.Success => OmniTone.Success,
        TaskExecutionStatus.Failed or TaskExecutionStatus.Timeout => OmniTone.Danger,
        TaskExecutionStatus.Running => OmniTone.Accent,
        TaskExecutionStatus.Cancelled => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    internal static OmniTone GetFailureBadge(string failureCode) =>
        failureCode == TaskFailureCodes.ToolError ? OmniTone.Warning : OmniTone.Danger;

    internal static OmniIconName GetFailureIcon(string failureCode) => failureCode switch
    {
        TaskFailureCodes.InfrastructureMismatch => OmniIconName.Wrench,
        TaskFailureCodes.TestsFailed => OmniIconName.Flask,
        TaskFailureCodes.BuildFailed => OmniIconName.Wrench,
        _ => OmniIconName.Warning
    };

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
        {
            try { await _hubConnection.InvokeAsync("LeaveAllServers"); } catch { /* best-effort */ }
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }
    }

    /// <summary>Recette R2-124: the export writes the source the cell links to.</summary>
    private Func<ServerTaskDto, object?> SourceExport => _sourceExport ??= task => task.PipelineRunId is { } pipelineRunId
        ? $"{L["PipelineRun"]} #{pipelineRunId}"
        : $"{L["Server"]}: {GetSourceServerName(task)}";
    private Func<ServerTaskDto, object?>? _sourceExport;
}
