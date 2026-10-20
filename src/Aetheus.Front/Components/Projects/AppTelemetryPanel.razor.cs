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

    [Parameter] public int ProjectId { get; set; }

    /// <summary>The application named by the page's <c>?app=</c>; the panel opens on it, and writes
    /// the reader's own choice back to the URL so the page can be linked and reloaded.</summary>
    [Parameter] public int? SelectedAppId { get; set; }

    private List<MonitoredAppDto> _apps = [];
    private int? _selectedAppId;
    private MonitoredAppDto? _selectedApp;
    private bool _canWrite;
    private bool _loading = true;

    protected override async Task OnInitializedAsync()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        await LoadAppsAsync();
        _loading = false;
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.Project);

    private async Task LoadAppsAsync()
    {
        try { _apps = await Api.Monitoring.GetMonitoredAppsAsync(ProjectId); }
        catch (HttpRequestException) { _apps = []; }

        if (SelectedAppId is { } wanted && _selectedAppId is null && _apps.Any(a => a.Id == wanted))
            _selectedAppId = wanted;
        if (_apps.Count > 0 && (_selectedAppId is null || _apps.All(a => a.Id != _selectedAppId)))
            _selectedAppId = _apps[0].Id;
        _selectedApp = _apps.FirstOrDefault(a => a.Id == _selectedAppId);
    }

    protected override void OnParametersSet()
    {
        if (SelectedAppId is not { } wanted || wanted == _selectedAppId || _apps.All(a => a.Id != wanted)) return;
        _selectedAppId = wanted;
        _selectedApp = _apps.FirstOrDefault(a => a.Id == wanted);
    }

    private void OnAppChanged()
    {
        _selectedApp = _apps.FirstOrDefault(a => a.Id == _selectedAppId);
        if (_selectedAppId is { } id && id != SelectedAppId)
            Nav.NavigateTo(Nav.GetUriWithQueryParameter("app", id), replace: true);
    }

    // The ingestion settings changed the app (key generated/revoked): reload so HasIngestKey/counters refresh.
    private async Task OnTelemetryChangedAsync()
    {
        await LoadAppsAsync();
        StateHasChanged();
    }

    public ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        return ValueTask.CompletedTask;
    }
}
