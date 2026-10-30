// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers;

public partial class Servers : IAsyncDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private IUserPreferences Preferences { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    private AetheusDataGrid<ServerDto>? _grid;
    private List<ServerDto> _servers = [];
    private readonly HashSet<int> _pendingDeleteIds = [];
    private int _totalCount;
    private bool _loading;
    private bool _loadFailed;
    internal bool _canWrite;
    // Column header filters are the list filter source.
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private ServerFilterValuesDto _filterValues = new();
    private IReadOnlyList<string> _agentCandidates = [];
    private Func<string, string>? _typeText;
    private Func<string, string>? _statusText;
    private Func<string, string>? _agentText;
    private AgentCompatibilitySummaryDto _compatibilitySummary = new();
    private HubConnection? _hubConnection;

    // --- Column chooser (S-FEAT-10): per-user column visibility kept per user on the server (R1-66) ---
    private bool _showColumnChooser;
    // Toggleable columns (Name/Status/Actions always visible). Default = all shown.
    private readonly Dictionary<string, bool> _columnVisibility = new()
    {
        ["Type"] = true,
        ["Hostname"] = true,
        ["OsDescription"] = true,
        ["AgentVersion"] = true,
        ["Tags"] = true,
        ["LastHeartbeat"] = true,
    };

    // Column key → localized label, for the chooser checkboxes.
    private IEnumerable<KeyValuePair<string, string>> _columnLabels =>
    [
        new("Type", L["Type"]),
        new("Hostname", L["Hostname"]),
        new("OsDescription", L["OS"]),
        new("AgentVersion", L["AgentVersion"]),
        new("Tags", L["Tags"]),
        new("LastHeartbeat", L["LastHeartbeat"]),
    ];

    internal bool IsColumnVisible(string key) =>
        !_columnVisibility.TryGetValue(key, out var visible) || visible;

    private void ToggleColumnChooser() => _showColumnChooser = !_showColumnChooser;

    private async Task OnColumnVisibilityChanged(string key, bool visible)
    {
        _columnVisibility[key] = visible;
        await PersistColumnVisibilityAsync();
    }

    private async Task PersistColumnVisibilityAsync()
    {
        // Store only the hidden columns (compact); absence => visible.
        var hidden = _columnVisibility.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
        await Preferences.SetAsync(PreferenceKeys.ServerHiddenColumns, string.Join(",", hidden));
    }

    private async Task LoadColumnVisibilityAsync()
    {
        var raw = await Preferences.GetAsync<string>(PreferenceKeys.ServerHiddenColumns);
        if (string.IsNullOrWhiteSpace(raw)) return;
        var hidden = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var key in _columnVisibility.Keys.ToList())
            _columnVisibility[key] = !hidden.Contains(key);
    }

    // Why a server is unreachable, surfaced client-side from the list payload
    // so operators don't need the Contact dialog. Shared with ServerDetail.
    private string OfflineReason(ServerDto server) =>
        ServerHeartbeatHelper.OfflineReason(L, server.LastHeartbeat);

    // Stale-while-revalidate cache key for one grid view (page + size + sort + every filter). The
    // default view (page 1 / size 20 / no filter / no sort) is what landing on /servers hits, so a
    // return visit renders the previous page instantly while the live fetch revalidates in the
    // background. SignalR patches keep the rendered rows fresh, so the cache only smooths the paint.
    private string CacheKey(int page, int pageSize, string? sortBy, bool sortDescending) =>
        $"servers:{page}:{pageSize}:{GridColumnFilters.CacheText(_columnFilters)}:{sortBy}:{sortDescending}";

    private Func<string, string> TypeText => _typeText ??= GridFilterText.ForEnum<ServerType>(L);
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<ServerStatus>(L);

    /// <summary>The agent column's one list reads a version as itself and a state as its
    /// label.</summary>
    private Func<string, string> AgentText => _agentText ??= value =>
        value.StartsWith(ServerAgentFilter.StatePrefix, StringComparison.Ordinal)
        && Enum.TryParse<AgentCompatibilityStatus>(value[ServerAgentFilter.StatePrefix.Length..], out var state)
            ? L[$"AgentCompatibility_{state}"].Value
            : value;

    private static IReadOnlyList<string> AgentStates { get; } =
        [.. Enum.GetValues<AgentCompatibilityStatus>().Select(ServerAgentFilter.State)];

    /// <summary>The values the OS, agent and tag filters offer: every value across the servers the user can read.</summary>
    private async Task LoadFilterValuesAsync()
    {
        try
        {
            _filterValues = await Api.Servers.GetServerFilterValuesAsync();
        }
        catch (HttpRequestException)
        {
            _filterValues = new ServerFilterValuesDto();
        }

        _agentCandidates = [.. AgentStates, .. _filterValues.AgentVersions];
    }

    protected override async Task OnInitializedAsync()
    {
        _agentCandidates = AgentStates;
        await LoadFilterValuesAsync();
        try
        {
            _compatibilitySummary = await Api.Servers.GetAgentCompatibilitySummaryAsync();
        }
        catch (HttpRequestException)
        {
            _compatibilitySummary = new AgentCompatibilitySummaryDto();
        }
        Breadcrumb.Set(new BreadcrumbItem(L["Servers"]));
        // Subscribe BEFORE reading: MainLayout loads permissions in parallel with
        // route activation, so the page can mount before they're ready. Without
        // the subscription, _canWrite would stay false until the next navigation
        // - stranding "Add Agent" / "Update All Agents" as permanently disabled.
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        // The shared grid requests its first page after its columns are registered.

        // Pre-seed the grid from the cached default view so the first paint shows the previous rows
        // instead of an empty spinner; OnLoadData then revalidates in the background (stale-while-revalidate).
        Cache.Seed<PaginatedResult<ServerDto>>(CacheKey(1, 20, null, false), ApplyServers);

        await LoadColumnVisibilityAsync();
        await StartHubAsync();
    }

    /// <summary>An agent update changes the compatibility count and the row's badge.</summary>
    private async Task RefreshAfterAgentUpdateAsync()
    {
        try
        {
            _compatibilitySummary = await Api.Servers.GetAgentCompatibilitySummaryAsync();
        }
        catch (HttpRequestException)
        {
            // The count stays as it was; the grid reload below still refreshes each row.
        }
        await InvalidateAndReloadGridAsync();
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() =>
        _canWrite = Permissions.CanWrite(Aetheus.Shared.Components.Auth.ResourceType.Server);

    private async Task StartHubAsync()
    {
        try
        {
            _hubConnection = HubFactory.Create("servers");
            // Lifecycle events (register/update/remove/offline) reload the whole grid - rare. Heartbeats
            // (~every 30s PER server) are coalesced into a single trailing reload (see OnHeartbeatAsync)
            // so a large fleet's clustered heartbeats can't thrash the grid.
            _hubConnection.On<ServerDto>("ServerRegistered", _ => InvokeAsync(InvalidateAndReloadGridAsync));
            _hubConnection.On<ServerDto>("ServerUpdated", _ => InvokeAsync(InvalidateAndReloadGridAsync));
            _hubConnection.On<int>("ServerRemoved", _ => InvokeAsync(InvalidateAndReloadGridAsync));
            _hubConnection.On<int>("ServerOffline", _ => InvokeAsync(InvalidateAndReloadGridAsync));
            // Refresh the compatibility count after the agent update.
            _hubConnection.On<int, int>("AgentUpdateConfirmed", (_, _) => InvokeAsync(RefreshAfterAgentUpdateAsync));
            _hubConnection.On<int, int>("AgentUpdateFailed", (_, _) => InvokeAsync(RefreshAfterAgentUpdateAsync));
            _hubConnection.On<int, ServerHeartbeatDto>("ServerHeartbeat", (id, heartbeat) => InvokeAsync(() => OnHeartbeatAsync(id, heartbeat)));
            // Group membership is per-connection and lost on auto-reconnect - re-join + reload to
            // catch broadcasts missed while disconnected (e.g. a server enrolled during a blip).
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinAllServers");
                await ReloadGridAsync();
            }));
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinAllServers");
        }
        catch
        {
            // SignalR is best-effort: fall back to manual refresh.
        }
    }

    private async Task ReloadGridAsync()
    {
        if (_grid is not null) await _grid.Reload();
    }

    private async Task InvalidateAndReloadGridAsync()
    {
        Cache.InvalidatePrefix("servers:");
        await ReloadGridAsync();
    }

    private const int HeartbeatDebounceMs = 5000;
    private readonly TrailingReloadCoalescer _heartbeatCoalescer = new(HeartbeatDebounceMs);

    // S-TECH-T3M8: a heartbeat only changes a server's live fields (Status / LastHeartbeat /
    // AgentVersion / AgentInstalledAt / DockerAvailable / InsecureTls). When it targets a server
    // already on the current page AND no status filter is active (so the patched Status can't move
    // the row in or out of the page), we replace that one row via the record's `with` - ServerDto
    // is init-only but freely copyable - and re-render just the grid: no LoadData round-trip, no
    // flicker, off-page heartbeats ignored outright. The full-reload path (still coalesced into a
    // single trailing reload per burst) is kept only where a targeted patch can't stay consistent:
    // an active status filter, where the new Online status may change page membership.
    private Task OnHeartbeatAsync(int serverId, ServerHeartbeatDto heartbeat)
    {
        // Any column filter can be moved by a heartbeat (status, agent version or state, last heartbeat).
        if (_columnFilters.Count > 0)
            return _heartbeatCoalescer.RequestAsync(ReloadGridAsync);

        var index = _servers.FindIndex(s => s.Id == serverId);
        if (index < 0)
            return Task.CompletedTask; // not on the current page → nothing visible changes

        _servers[index] = _servers[index] with
        {
            Status = ServerStatus.Online,
            LastHeartbeat = DateTime.Now,
            AgentVersion = heartbeat.AgentVersion ?? _servers[index].AgentVersion,
            AgentInstalledAt = heartbeat.AgentInstalledAt ?? _servers[index].AgentInstalledAt,
            DockerAvailable = heartbeat.DockerAvailable,
            InsecureTls = heartbeat.InsecureTls
        };
        StateHasChanged();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        if (_hubConnection is not null)
        {
            await _hubConnection.LeaveAllServersAndDisposeAsync();
            _hubConnection = null;
        }
    }

    private void ApplyServers(PaginatedResult<ServerDto> result)
    {
        var optimisticRowsStillReturned = result.Items.Count(server => _pendingDeleteIds.Contains(server.Id));
        _servers = result.Items.Where(server => !_pendingDeleteIds.Contains(server.Id)).ToList();
        _totalCount = Math.Max(0, result.TotalCount - optimisticRowsStillReturned);
    }

    /// <summary>One page of the export, with the grid's sort (none by default) and column
    /// filters.</summary>
    private Task<PaginatedResult<ServerDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var sort = args.Sorts?.FirstOrDefault();
        return Api.Servers.GetServersAsync(page, pageSize, sortBy: sort?.Property,
            sortDescending: sort?.SortOrder == GridSortOrder.Descending, filters: args.ToApiFilters());
    }

    private async Task OnLoadData(GridLoadArgs args)
    {
        _loadFailed = false;
        var (page, pageSize) = args.ToPageRequest();
        _columnFilters = args.ToApiFilters();
        var filters = _columnFilters;

        string? sortBy = null;
        var sortDescending = false;
        if (args.Sorts?.Any() == true)
        {
            var sort = args.Sorts.First();
            sortBy = sort.Property;
            sortDescending = sort.SortOrder == GridSortOrder.Descending;
        }

        // Stale-while-revalidate: a cached view renders instantly with no spinner while the live fetch
        // revalidates; only a cold (uncached) view shows the spinner. A cache hit can race with an
        // in-flight SignalR heartbeat patch, but the background fetch overwrites it either way.
        await Cache.RevalidateAsync(
            CacheKey(page, pageSize, sortBy, sortDescending),
            () => Api.Servers.GetServersAsync(
                page, pageSize, sortBy: sortBy, sortDescending: sortDescending, filters: filters),
            ApplyServers,
            loading => _loading = loading,
            () => InvokeAsync(StateHasChanged),
            _ => _loadFailed = true);
    }

    /// <summary>A tag clicked in a row filters the Tags column on it, visibly, in its
    /// header.</summary>
    internal async Task FilterByTag(string tag)
    {
        if (_grid?.Grid is { } grid)
            await grid.SetFiltersAsync(new Dictionary<string, string?> { [ServerTagsColumn] = tag });
    }

    internal const string ServerTagsColumn = "Tags";

    private void GoToAddAgent() => Navigation.NavigateTo("/servers/add-agent");

    private Task OnContactAgent(ServerDto server)
        => Dialog.OpenAsync<ContactAgentDialog>(
            L["ContactAgent"],
            new Dictionary<string, object?> { { "ServerId", server.Id }, { "ServerName", server.Name } },
            new OmniDialogOptions { Width = "480px", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });

    // Retired servers are excluded from this grid and listed in the dialog that offers permanent
    // deletion.
    private Task OpenRetiredServersDialog()
        => Dialog.OpenAsync<RetiredServersDialog>(
            L["RetiredServers"],
            new Dictionary<string, object?>(),
            new OmniDialogOptions { Width = "900px", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });

    private Task OpenPortCheckDialog()
        => Dialog.OpenAsync<Aetheus.Front.Components.Shared.PortCheckDialog>(
            L["CheckPorts"],
            new Dictionary<string, object?>(),
            new OmniDialogOptions { Width = "640px", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });

    private async Task OnUpdateAllAgents()
    {
        AgentUpdateAllPreviewDto? preview;
        try
        {
            preview = await Api.Servers.PreviewUpdateAllAgentsAsync();
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "UpdateAgentFailed");
            return;
        }
        if (preview is null)
        {
            Toast.Error("Error", "UpdateAgentFailed");
            return;
        }

        var confirmed = await Dialog.Confirm(
            string.Format(
                L["UpdateAllAgentsPreview"],
                preview.TargetVersion,
                preview.AffectedCount,
                preview.AlreadyUpToDateCount,
                preview.OfflineCount,
                preview.IncompatibleCount,
                preview.BusyCount),
            L["UpdateAllAgents"],
            new OmniConfirmOptions { OkButtonText = L["Update"], CancelButtonText = L["GoBack"] });
        if (confirmed != true) return;

        var result = await Api.Servers.UpdateAllAgentsAsync();
        if (result is not null)
            Toast.Success("UpdateAllAgents", "UpdateAllAgentsQueued", result.QueuedCount);
        else
            Toast.Error("Error", "UpdateAgentFailed");
    }

    // The row action retires the server while preserving links. Permanent deletion is available
    // only for retired servers in RetiredServersDialog.
    private async Task OnRetireServer(ServerDto server)
    {
        if (_pendingDeleteIds.Contains(server.Id)) return;

        var confirmed = await Dialog.Confirm(
            string.Format(L["RetireServerConfirm"], server.Name),
            L["RetireServer"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Retire"], CancelButtonText = L["GoBack"] });
        if (confirmed != true) return;

        await Veil.RunAsync(() => RetireServerConfirmedAsync(server));
    }

    internal async Task RetireServerConfirmedAsync(ServerDto server)
    {
        if (_pendingDeleteIds.Contains(server.Id)) return;

        // Hide the row immediately while the backend retires the server; ApplyServers keeps
        // realtime/cache reloads from briefly reintroducing it. A failed request removes the optimistic
        // guard and reloads the persisted state, so the row is restored instead of silently disappearing.
        var removedIndex = _servers.FindIndex(item => item.Id == server.Id);
        var deleteSucceeded = false;
        _pendingDeleteIds.Add(server.Id);
        // Replace the collection reference so the grid observes the data change immediately; mutating
        // the existing List in place can leave its internal row view unchanged until the next reload.
        _servers = _servers.Where(item => item.Id != server.Id).ToList();
        _totalCount = Math.Max(0, _totalCount - 1);
        Cache.InvalidatePrefix("servers:");
        StateHasChanged();

        try
        {
            var retired = await Api.Servers.RetireServerAsync(server.Id);
            if (retired)
            {
                deleteSucceeded = true;
                Toast.Success(L["ServerRetired"]);
            }
            else
            {
                Toast.Error("Error", "ServerRetireFailed");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Toast.Error("Error", "ServerRetireFailed");
        }
        finally
        {
            _pendingDeleteIds.Remove(server.Id);
            Cache.InvalidatePrefix("servers:");
            if (!deleteSucceeded && removedIndex >= 0 && _servers.All(item => item.Id != server.Id))
            {
                var restored = _servers.ToList();
                restored.Insert(Math.Min(removedIndex, restored.Count), server);
                _servers = restored;
                _totalCount++;
                StateHasChanged();
            }
            if (_grid is not null) await _grid.Reload();
        }
    }

    /// <summary>The export writes every tag of the server, the cell shows three.</summary>
    private static readonly Func<ServerDto, object?> TagsExport = server => string.Join(", ", server.Tags);
}
