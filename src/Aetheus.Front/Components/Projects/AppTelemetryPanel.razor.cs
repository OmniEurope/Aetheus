// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects;

/// <summary>
/// ADR-021 phases 2-4: OTLP telemetry panel for a project. An app picker selects one monitored app,
/// then sub-tabs (Visitors / Pages / Metrics / Logs / Errors / Ingestion) show its telemetry. RBAC inherited from the Project.
/// </summary>
public partial class AppTelemetryPanel : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    [Parameter] public int ProjectId { get; set; }

    /// <summary>The application named by the page's <c>?app=</c>; the panel opens on it, and writes
    /// the reader's own choice back to the URL so the page can be linked and reloaded.</summary>
    [Parameter] public int? SelectedAppId { get; set; }

    private List<MonitoredAppDto> _apps = [];
    private int? _selectedAppId;
    private MonitoredAppDto? _selectedApp;
    private bool _canWrite;
    private bool _loading = true;
    private int _settingsVersion;
    private AppTelemetrySendIntervalDto? _interval;
    private string? _intervalSetting;
    private int? _intervalAppId;

    protected override async Task OnInitializedAsync()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        await LoadAppsAsync();
        _loading = false;
        StateHasChanged();
        await LoadIntervalAsync();
        await StartHubAsync();
    }

    // The monitored-apps list above this panel follows the project's EntityChanged pushes (the backend broadcasts
    // on every app creation, edition, deletion and status change); the panel follows the same push, so an app added
    // from the header menu no longer leaves it saying "no application supervised" until a full reload.
    private const int ReloadDebounceMs = 300;
    private readonly TrailingReloadCoalescer _reloadCoalescer = new(ReloadDebounceMs);
    private readonly CancellationTokenSource _lifetime = new();
    private HubConnection? _hub;

    private async Task StartHubAsync()
    {
        try
        {
            var hub = HubFactory.Create("entities");
            _hub = hub;
            hub.On<ResourceType, int, string>("EntityChanged", (type, id, _) =>
                type == ResourceType.Project && id == ProjectId ? RequestReloadAsync() : Task.CompletedTask);
            hub.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await hub.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
                await ReloadAppsAsync();
            }));
            await hub.StartAsync(_lifetime.Token);
            await hub.InvokeAsync("JoinEntityUpdates", ResourceType.Project, _lifetime.Token);
        }
        catch
        {
            // Hub unavailable: the panel keeps the list it loaded.
        }
    }

    internal Task RequestReloadAsync() => _reloadCoalescer.RequestAsync(() => InvokeAsync(ReloadAppsAsync), _lifetime.Token);

    // A read that fails keeps the list on screen: a transient error must not turn into "no application supervised".
    private async Task ReloadAppsAsync()
    {
        await LoadAppsAsync(keepOnFailure: true);
        await LoadIntervalAsync();
        StateHasChanged();
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.Project);

    private async Task LoadAppsAsync(bool keepOnFailure = false)
    {
        try { _apps = await Api.Monitoring.GetMonitoredAppsAsync(ProjectId); }
        catch (HttpRequestException)
        {
            if (!keepOnFailure) _apps = [];
        }

        if (SelectedAppId is { } wanted && _selectedAppId is null && _apps.Any(a => a.Id == wanted))
            _selectedAppId = wanted;
        if (_apps.Count > 0 && (_selectedAppId is null || _apps.All(a => a.Id != _selectedAppId)))
            _selectedAppId = _apps[0].Id;
        _selectedApp = _apps.FirstOrDefault(a => a.Id == _selectedAppId);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (SelectedAppId is not { } wanted || wanted == _selectedAppId || _apps.All(a => a.Id != wanted)) return;
        _selectedAppId = wanted;
        _selectedApp = _apps.FirstOrDefault(a => a.Id == wanted);
        await LoadIntervalAsync();
    }

    private async Task OnAppChangedAsync()
    {
        _selectedApp = _apps.FirstOrDefault(a => a.Id == _selectedAppId);
        if (_selectedAppId is { } id && id != SelectedAppId)
            Nav.NavigateTo(Nav.GetUriWithQueryParameter("app", id), replace: true);
        await LoadIntervalAsync();
    }

    /// <summary>Reads the send interval of the selected application once per application: its setting is the
    /// « ? » help of the picker, and the note under it states only what departs from it.</summary>
    private async Task LoadIntervalAsync()
    {
        var appId = _selectedApp?.Id;
        if (appId == _intervalAppId) return;
        _intervalAppId = appId;
        _interval = null;
        _intervalSetting = null;
        if (appId is not { } id) return;
        try
        {
            var interval = await Api.Monitoring.GetAppSendIntervalAsync(id);
            if (_intervalAppId != id || interval is null) return;
            _interval = interval;
            _intervalSetting = TelemetrySendIntervalText.Setting(L, interval.ServerIntervalSeconds, interval.Profile, interval.IsProjectOverride);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            // A help, not the view: without it the telemetry still reads.
        }
    }

    // The ingestion settings changed the app (key generated/revoked): reload so HasIngestKey/counters refresh.
    private async Task OnTelemetryChangedAsync()
    {
        _settingsVersion++;
        await LoadAppsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        await _lifetime.CancelAsync();
        if (_hub is { } hub)
        {
            _hub = null;
            await hub.LeaveEntityUpdatesAndDisposeAsync(ResourceType.Project);
        }
        _lifetime.Dispose();
    }
}
