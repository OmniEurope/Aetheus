// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects;

/// <summary>
/// ADR-021 phase 1: monitored-apps list for a project (availability). Self-loading; the <c>entities</c>
/// hub keeps statuses live (the backend broadcasts a Project "Updated" on every app status transition).
/// </summary>
public partial class MonitoredAppsList : IAsyncDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private UiActions Ui { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    [Parameter] public int ProjectId { get; set; }

    /// <summary>The project header's menu outlet (absent when the list is hosted outside the project layout).</summary>
    [CascadingParameter] public ProjectSectionMenu? SectionMenu { get; set; }

    private List<MonitoredAppDto> _apps = [];
    private AetheusDataGrid<MonitoredAppDto>? _grid;
    // Header filter texts and the hosting-server value the filter lists, built once so the columns
    // see the same delegates on every render.
    private Func<string, string>? _statusText;
    private Func<MonitoredAppDto, object?>? _serverValue;
    private Func<string, string> StatusText => _statusText ??= value =>
        Enum.TryParse<AppHealthStatus>(value, ignoreCase: true, out var status) ? StatusLabel(status) : value;
    private Func<MonitoredAppDto, object?> ServerValue => _serverValue ??= app => FormatServer(app);
    // S-UX-SPKL: 24 h availability samples per app, for the inline sparkline.
    private Dictionary<int, IReadOnlyList<AppHealthSampleDto>> _samples = [];
    private bool _loading = true;
    private bool _canWrite;
    private HubConnection? _hubConnection;
    private readonly CancellationTokenSource _lifetime = new();
    private string CacheKey => $"monitored-apps:{ProjectId}";
    private sealed record CachedMonitoringData(
        List<MonitoredAppDto> Apps,
        Dictionary<int, IReadOnlyList<AppHealthSampleDto>> Samples);
    // Challenge (Élevé): the backend emits one Project "Updated" broadcast PER app status transition, so a
    // fleet-wide blip fires N broadcasts; each reload also fans out N sample fetches. Coalesce the burst
    // into a single trailing-edge reload (same pattern as Servers/Pipelines/Releases lists).
    private const int ReloadDebounceMs = 400;
    private readonly TrailingReloadCoalescer _reloadCoalescer = new(ReloadDebounceMs);

    private Task RequestReloadAsync() =>
        _reloadCoalescer.RequestAsync(() => InvokeAsync(async () => { await LoadAppsAsync(live: true); StateHasChanged(); }));

    private IReadOnlyList<AppHealthSampleDto> SamplesFor(int appId) =>
        _samples.TryGetValue(appId, out var s) ? s : [];

    protected override async Task OnInitializedAsync()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        if (Cache.TryGet<CachedMonitoringData>(CacheKey, out var cached) && cached is not null)
        {
            _apps = cached.Apps;
            _samples = cached.Samples;
            _loading = false;
        }
        await LoadAppsAsync();
        _loading = false;
        // The hub only refreshes the list: it is drawn from what was read, never after the hub has started
        // (a start that stalls kept the grid on its loading bar).
        StateHasChanged();
        await StartHubAsync();
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite()
    {
        _canWrite = Permissions.CanWrite(ResourceType.Project);
        PublishHeader();
    }

    /// <summary>The project header's menu gets Add (one delegate, so republishing raises nothing) and its Settings
    /// button turns into the blue main action; read-only readers get neither.</summary>
    private void PublishHeader()
    {
        if (SectionMenu is null) return;
        SectionMenu.Set(_canWrite
            ? [new ProjectSectionMenuAction("add", L["AddMonitoredApp"], null, false, _openCreate ??= OpenCreateDialogAsync)]
            : []);
        SectionMenu.SetSettingsPrimary(true);
    }

    private Func<Task>? _openCreate;

    /// <param name="live">a live event; the grid notes the rows it holds before the new list lands,
    /// so an app added elsewhere reads bold for a few seconds.</param>
    private async Task LoadAppsAsync(bool live = false)
    {
        try
        {
            // FRONT-020: a list that arrives after a sign-out is neither cached nor shown.
            var ticket = Cache.Begin();
            var apps = await Api.Monitoring.GetMonitoredAppsAsync(ProjectId);
            if (!Cache.IsCurrent(ticket)) return;
            if (live && _grid is not null) await _grid.Refresh();
            _apps = apps;
            await LoadSamplesAsync();
            Cache.TryStore(ticket, CacheKey, new CachedMonitoringData(_apps, _samples));
        }
        catch (HttpRequestException)
        {
            // expired JWT / transient - AuthProvider handles redirect; keep the last known list.
        }
    }

    // Fetch the 24 h availability samples for every listed app in parallel (few apps per project). Best
    // effort: a failure just leaves the sparkline empty, the list still renders.
    private async Task LoadSamplesAsync()
    {
        try
        {
            var loaded = await Task.WhenAll(_apps.Select(async a =>
                (a.Id, Samples: (IReadOnlyList<AppHealthSampleDto>)await Api.Monitoring.GetMonitoredAppSamplesAsync(a.Id))));
            _samples = loaded.ToDictionary(x => x.Id, x => x.Samples);
        }
        catch (HttpRequestException)
        {
            // keep the last known samples
        }
    }

    private Task OpenCreateDialogAsync() => OpenEditDialogAsync(null);

    private async Task OpenEditDialogAsync(MonitoredAppDto? app)
    {
        var result = await Dialog.OpenAsync<MonitoredAppFormDialog>(
            app is null ? L["AddMonitoredApp"] : L["Edit"],
            new Dictionary<string, object?> { ["ProjectId"] = ProjectId, ["App"] = app },
            new OmniDialogOptions { Width = "760px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });

        if (result is MonitoredAppDto saved)
            await UpsertAppAsync(saved);
    }

    /// <summary>R-10: the created or saved app shows at once from the answer of the action, the list is read again
    /// behind it without blocking. The answer carries no sparkline, so a known app keeps its samples.</summary>
    private Task UpsertAppAsync(MonitoredAppDto saved)
    {
        var known = _apps.FindIndex(held => held.Id == saved.Id);
        _apps = known < 0 ? [.. _apps, saved] : [.. _apps.Select(held => held.Id == saved.Id ? saved : held)];
        // The cached list would seed the previous state again until the read below stores the real one.
        Cache.Invalidate(CacheKey);
        _ = InvokeAsync(async () => { await LoadAppsAsync(); StateHasChanged(); });
        return InvokeAsync(StateHasChanged);
    }

    private async Task DeleteAppAsync(MonitoredAppDto app)
    {
        var confirmed = await Dialog.Confirm(
            L["DeleteMonitoredAppConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true)
            return;

        await Veil.RunAsync(() => Ui.RunAsync(
            () => Api.Monitoring.DeleteMonitoredAppAsync(app.Id),
            "MonitoredAppDeleted",
            () => RemoveAppAsync(app),
            errorKey: "DeleteFailed",
            successTitleKey: "Deleted"));
    }

    /// <summary>R-10: the deleted app leaves the grid at once, the list is read again behind it without blocking.
    /// A refused deletion never gets here, so the row stays.</summary>
    private Task RemoveAppAsync(MonitoredAppDto app)
    {
        _apps = [.. _apps.Where(held => held.Id != app.Id)];
        _samples.Remove(app.Id);
        // The cached list would seed the deleted app again until the read below stores the real one.
        Cache.Invalidate(CacheKey);
        _ = InvokeAsync(async () => { await LoadAppsAsync(); StateHasChanged(); });
        return InvokeAsync(StateHasChanged);
    }

    private async Task StartHubAsync()
    {
        try
        {
            _hubConnection = HubFactory.Create("entities");
            _hubConnection.On<ResourceType, int, string>("EntityChanged", (type, id, _) =>
                type == ResourceType.Project && id == ProjectId ? RequestReloadAsync() : Task.CompletedTask);
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
                await LoadAppsAsync(live: true);
                StateHasChanged();
            }));
            // Bound to the page: leaving it cancels a negotiate that does not answer instead of holding the
            // disposal until the HTTP timeout (two minutes).
            await _hubConnection.StartAsync(_lifetime.Token);
            await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Project, _lifetime.Token);
        }
        catch { /* Hub unavailable - degrade to static */ }
    }

    private string StatusLabel(AppHealthStatus status) => L[$"AppHealth{status}"];

    private string FormatUptime(double? ratio) =>
        ratio is null ? L["NotAvailable"].Value : (ratio.Value * 100).ToString("0.0") + "%";

    private string FormatServer(MonitoredAppDto app) => MonitoredAppServerLabel.Format(app, L);

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        SectionMenu?.Clear();
        SectionMenu?.SetSettingsPrimary(false);
        await _lifetime.CancelAsync();
        if (_hubConnection is not null)
        {
            await _hubConnection.LeaveEntityUpdatesAndDisposeAsync(ResourceType.Project);
            _hubConnection = null;
        }
        _lifetime.Dispose();
    }

    /// <summary>The export writes the uptimes the cells show.</summary>
    private Func<MonitoredAppDto, object?> Uptime24hExport => _uptime24hExport ??= app => FormatUptime(app.Uptime24h);
    private Func<MonitoredAppDto, object?> Uptime7dExport => _uptime7dExport ??= app => FormatUptime(app.Uptime7d);
    private Func<MonitoredAppDto, object?>? _uptime24hExport;
    private Func<MonitoredAppDto, object?>? _uptime7dExport;
}
