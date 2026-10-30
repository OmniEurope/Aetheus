// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// 0-a: shared Variable Libraries list, reused across the 3 scopes. The optional
/// <see cref="ProjectId"/> / <see cref="ServerId"/> parameters pick the source: global/project are
/// server-paginated via <c>GetVariableLibrariesAsync</c> (the endpoint supports a <c>projectId</c>
/// filter); server detail is self-loaded via <c>GetServerVariableLibrariesAsync</c> and paged
/// in-memory (no server-side serverId paging) with a <see cref="ListCacheService"/> entry for
/// stale-while-revalidate. Self-loading - no dependency on the detail loaders.
/// </summary>
public partial class VariableLibrariesList : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    /// <summary>Project filter (project detail scope). Mutually exclusive with <see cref="ServerId"/>.</summary>
    [Parameter] public int? ProjectId { get; set; }

    /// <summary>Server filter (server detail scope). When both are null the list is global (unfiltered).</summary>
    [Parameter] public int? ServerId { get; set; }

    private bool IsServerScope => ServerId.HasValue;
    private bool IsProjectScope => ProjectId.HasValue;
    private bool IsGlobal => !ProjectId.HasValue && !ServerId.HasValue;

    private List<VariableLibraryDto> _libraries = [];
    private List<VariableLibraryDto> _serverAll = []; // server scope: full list, paged client-side
    private int _totalCount;
    private bool _loading;
    private bool _canWrite;
    // The project column's checkable filter lists every project owning a library the caller can
    // read: served by the API for the global list, taken from the loaded list in server scope.
    private VariableLibraryFilterValuesDto _filterValues = new();
    private Dictionary<int, List<ResourceUsageUserDto>> _usage = [];
    private bool _usageLoaded;
    private HubConnection? _hubConnection;

    private string CacheKey => $"variable-libraries:server:{ServerId}";

    // Server-paginated (global/project) scope cache key, distinct from the server-detail CacheKey above.
    // The sort is part of the key: two orders sharing one entry means the second is served the first
    // one's rows, which is indistinguishable from a sort that does nothing.
    private string PagedCacheKey(int page, int pageSize, IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters,
        string? sortBy = null, bool sortDescending = false) =>
        $"variable-libraries:{ProjectId}:{page}:{pageSize}:{GridColumnFilters.CacheText(filters)}:{sortBy}:{sortDescending}";

    private void ApplyLibrariesPage(PaginatedResult<VariableLibraryDto> result)
    {
        _libraries = result.Items;
        _totalCount = result.TotalCount;
    }

    protected override async Task OnInitializedAsync()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();

        if (IsServerScope && Cache.TryGet<List<VariableLibraryDto>>(CacheKey, out var cached) && cached is not null)
        {
            _serverAll = cached;
            _totalCount = cached.Count;
        }
        else if (!IsServerScope)
        {
            // Stale-while-revalidate: pre-seed the paginated view so the first paint is instant.
            Cache.Seed<PaginatedResult<VariableLibraryDto>>(PagedCacheKey(1, 20, null), ApplyLibrariesPage);
        }

        if (IsServerScope)
        {
            try { await LoadServerLibrariesAsync(); }
            catch (HttpRequestException) { } // 401 on expired JWT - redirect handled by AuthProvider
        }

        await LoadFilterValuesAsync();
        await LoadUsageAsync();
        await StartHubAsync();
    }

    /// <summary>Who reads each library (R-65). A failed call leaves the column empty rather than claiming
    /// every library is unused.</summary>
    private async Task LoadUsageAsync()
    {
        try
        {
            _usage = (await Api.Variables.GetResourceUsageAsync()).Libraries;
            _usageLoaded = true;
            StateHasChanged();
        }
        catch (HttpRequestException) { } // the list stays usable without the usage column
    }

    private IReadOnlyList<ResourceUsageUserDto>? UsersOf(int libraryId) =>
        _usage.TryGetValue(libraryId, out var users) ? users : null;

    /// <summary>The detail page of a library, remembering this list so its Back and its deletion return here.</summary>
    private string LibraryHref(int id) => LocalReturnUrl.Append($"/variable-libraries/{id}", LocalReturnUrl.Current(Nav));

    private async Task LoadFilterValuesAsync()
    {
        if (!IsGlobal) return;
        try { _filterValues = await Api.Variables.GetVariableLibraryFilterValuesAsync(); }
        catch (HttpRequestException) { } // the column still filters by typed text without the list
    }

    private async Task LoadServerLibrariesAsync()
    {
        // FRONT-020: a list that arrives after a sign-out is neither cached nor shown.
        var ticket = Cache.Begin();
        var libraries = await Api.Servers.GetServerVariableLibrariesAsync(ServerId!.Value);
        if (!Cache.TryStore(ticket, CacheKey, libraries)) return;
        _serverAll = libraries;
        _totalCount = _serverAll.Count;
        _filterValues = new VariableLibraryFilterValuesDto
        {
            ProjectNames = [.. _serverAll.Select(item => item.ProjectName).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]
        };
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.VariableLibrary);

    /// <summary>One page of the export (the libraries, not their entries), in the scope of the
    /// list, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<VariableLibraryDto>> ReadExportAsync(GridLoadArgs args)
    {
        if (IsServerScope)
        {
            return Task.FromResult(new PaginatedResult<VariableLibraryDto>
            {
                Items = args.ToClientPage(_serverAll),
                TotalCount = args.ClientFilteredCount(_serverAll)
            });
        }

        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(VariableLibraryDto.Name));
        return Api.Variables.GetVariableLibrariesAsync(page: page, pageSize: pageSize, projectId: ProjectId,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    private async Task OnLoadData(GridLoadArgs args)
    {
        if (TryApplyServerPage(args)) return;

        // Every header filter goes to the API, which checks it against its column map.
        var filters = args.ToApiFilters();
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(VariableLibraryDto.Name));
        await Cache.RevalidateAsync(
            PagedCacheKey(page, pageSize, filters, sortBy, sortDescending),
            () => Api.Variables.GetVariableLibrariesAsync(page: page, pageSize: pageSize, projectId: ProjectId,
                sortBy: sortBy, sortDescending: sortDescending, filters: filters),
            ApplyLibrariesPage,
            loading => _loading = loading,
            () => InvokeAsync(StateHasChanged));
    }

    private bool TryApplyServerPage(GridLoadArgs args)
    {
        if (!IsServerScope) return false;
        _libraries = args.ToClientPage(_serverAll);
        _totalCount = args.ClientFilteredCount(_serverAll);
        return true;
    }

    private AetheusDataGrid<VariableLibraryDto>? _grid;

    /// <summary>A change pushed by the hub refreshes the grid quietly, its page, sort, filters and
    /// scroll kept, and brings a new project into the project filter.</summary>
    private async Task RefreshGridAsync()
    {
        await LoadFilterValuesAsync();
        if (_grid is not null) await _grid.Refresh();
    }

    private void NewLibrary() => Nav.NavigateTo(IsProjectScope
        ? $"/variable-libraries/new?projectId={ProjectId}"
        : "/variable-libraries/new");

    private async Task StartHubAsync()
    {
        try
        {
            _hubConnection = HubFactory.Create("entities");
            _hubConnection.On<ResourceType, int, string>("EntityChanged", (type, _, _) =>
            {
                // R-64: a pipeline created or changed afterwards changes who reads each library.
                if (type == ResourceType.Pipeline) return InvokeAsync(LoadUsageAsync);
                if (type != ResourceType.VariableLibrary) return Task.CompletedTask;
                return InvokeAsync(async () =>
                {
                    if (IsServerScope)
                    {
                        try { await LoadServerLibrariesAsync(); } catch (HttpRequestException) { } // hub-triggered reload - silent on auth failure
                    }
                    await RefreshGridAsync();
                });
            });
            // Group membership is per-connection and lost on auto-reconnect - re-join + reload.
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.VariableLibrary);
                await JoinPipelineUpdatesAsync(_hubConnection);
                if (IsServerScope)
                {
                    try { await LoadServerLibrariesAsync(); } catch (HttpRequestException) { } // hub-triggered reload - silent on auth failure
                }
                await RefreshGridAsync();
                await LoadUsageAsync();
            }));
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.VariableLibrary);
            await JoinPipelineUpdatesAsync(_hubConnection);
        }
        catch { /* Hub unavailable - degrade to static */ }
    }

    /// <summary>The pipelines only feed the "used by" column; a caller who may not read any pipeline keeps the
    /// library updates without them.</summary>
    private static async Task JoinPipelineUpdatesAsync(HubConnection hub)
    {
        try { await hub.InvokeAsync("JoinEntityUpdates", ResourceType.Pipeline); }
        catch (Microsoft.AspNetCore.SignalR.HubException) { /* Access denied to pipelines: the column keeps what it loaded. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        if (_hubConnection is not null)
        {
            try { await _hubConnection.InvokeAsync("LeaveEntityUpdates", ResourceType.Pipeline); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
                or Microsoft.AspNetCore.SignalR.HubException)
            { /* Leaving a group is best-effort when the transport is already unavailable. */ }
            await _hubConnection.LeaveEntityUpdatesAndDisposeAsync(ResourceType.VariableLibrary);
            _hubConnection = null;
        }
    }
}
