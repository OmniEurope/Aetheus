// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects;

/// <summary>
/// ADR-021 phase 1: monitored-apps list for a project (availability). Self-loading; the <c>entities</c>
/// hub keeps statuses live (the backend broadcasts a Project "Updated" on every app status transition).
/// </summary>
public partial class MonitoredAppsList : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private UiActions Ui { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    [Parameter] public int ProjectId { get; set; }

    private List<MonitoredAppDto> _apps = [];
    private AetheusDataGrid<MonitoredAppDto>? _grid;
    // Recette R-210: header filter texts and the hosting-server value the filter lists, built once so
    // the columns see the same delegates on every render.
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
        await StartHubAsync();
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.Project);

    /// <param name="live">Recette R-227: a live event; the grid notes the rows it holds before the new
    /// list lands, so an app added elsewhere reads bold for a few seconds.</param>
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

        if (result is true)
        {
            await LoadAppsAsync();
            StateHasChanged();
        }
    }

    private async Task DeleteAppAsync(MonitoredAppDto app)
    {
        var confirmed = await Dialog.Confirm(
            L["DeleteMonitoredAppConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true)
            return;

        await Ui.RunAsync(
            () => Api.Monitoring.DeleteMonitoredAppAsync(app.Id),
            "MonitoredAppDeleted",
            async () => await LoadAppsAsync(),
            errorKey: "DeleteFailed",
            successTitleKey: "Deleted");
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
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
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
        if (_hubConnection is not null)
        {
            await _hubConnection.LeaveEntityUpdatesAndDisposeAsync(ResourceType.Project);
            _hubConnection = null;
        }
    }

    /// <summary>Recette R2-124: the export writes the uptimes the cells show.</summary>
    private Func<MonitoredAppDto, object?> Uptime24hExport => _uptime24hExport ??= app => FormatUptime(app.Uptime24h);
    private Func<MonitoredAppDto, object?> Uptime7dExport => _uptime7dExport ??= app => FormatUptime(app.Uptime7d);
    private Func<MonitoredAppDto, object?>? _uptime24hExport;
    private Func<MonitoredAppDto, object?>? _uptime7dExport;
}
