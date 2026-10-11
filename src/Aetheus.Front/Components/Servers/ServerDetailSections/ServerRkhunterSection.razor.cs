// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerRkhunterSection : IAsyncDisposable
{
    [Parameter] public int ServerId { get; set; }
    [Parameter] public RkhunterDataDto Rk { get; set; } = new();
    [Parameter] public EventCallback OnRefreshRequested { get; set; }

    // SITE-OFFLINE-001: the host page passes the RKHunter data only; the server (and its agent's
    // presence) comes from the loader the server detail layout cascades to every section.
    [CascadingParameter(Name = "ServerLoader")] public ServerDetailLoader? Loader { get; set; }

    private ServerDetailDto? AgentServer => Loader?.Server;
    private bool AgentOffline => AgentServer is { } server && AgentOfflineGuard.IsOffline(server);
    private bool BlockedByOfflineAgent() => AgentServer is { } server && AgentOfflineGuard.Blocks(server, Toast);

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    private bool _actionRunning;
    private string _setupMailOnWarning = string.Empty;
    private List<RkhunterWarningDto> _warnings = [];
    private List<RkhunterScanResultDto> _scanHistory = [];
    private OmniDataGrid<RkhunterWarningDto>? _warningsGrid;
    private OmniDataGrid<RkhunterScanResultDto>? _historyGrid;

    // Recette R-210: the scan status filter shows the same words as the cells, built once.
    private Func<string, string>? _scanStatusText;
    private Func<string, string> ScanStatusText => _scanStatusText ??= LocalizeScanStatus;
    private string _scheduleCron = string.Empty;
    private bool _savingSchedule;
    private int? _loadedServerId;
    private bool _scheduleDirty;

    // R-181: replaces the two Refresh buttons. Heartbeats only: the RKHunter task completions already
    // reach HandleTaskCompletedAsync through the page's loader, reloading on every task would double them.
    private ServerLiveFeed? _liveFeed;
    internal ServerLiveFeed? LiveFeed => _liveFeed;
    internal IReadOnlyList<RkhunterWarningDto> Warnings => _warnings;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId != ServerId)
        {
            _loadedServerId = ServerId;
            _warnings = [];
            _scanHistory = [];
            _setupMailOnWarning = string.Empty;
            _scheduleCron = Rk.ScanScheduleCron ?? string.Empty;
            _scheduleDirty = false;
            _liveFeed ??= new ServerLiveFeed(HubFactory);
            _ = _liveFeed.StartAsync(ServerId, ServerLiveFeedTriggers.Heartbeat,
                () => InvokeAsync(ReloadFromPushAsync));
            if (Rk.IsInstalled)
                await LoadResultsAsync();
            return;
        }

        if (!_scheduleDirty)
            _scheduleCron = Rk.ScanScheduleCron ?? string.Empty;
    }

    private void OnScheduleChanged(string? value)
    {
        _scheduleCron = value ?? string.Empty;
        _scheduleDirty = true;
    }

    private async Task ExecuteActionAsync(RkhunterAction action)
    {
        if (BlockedByOfflineAgent()) return;
        _actionRunning = true;
        var success = await Api.Security.ExecuteRkhunterActionAsync(ServerId, new RkhunterActionRequest { Action = action });
        _actionRunning = false;
        if (success)
        {
            Toast.Success("TaskCreated");
            await OnRefreshRequested.InvokeAsync();
        }
        else
            Toast.Error("Error", "ActionFailed");
    }

    private async Task SetupAsync()
    {
        if (BlockedByOfflineAgent()) return;
        var request = new RkhunterSetupRequest
        {
            MailOnWarning = _setupMailOnWarning?.Trim() ?? string.Empty
        };
        var success = await Api.Security.SetupRkhunterAsync(ServerId, request);
        if (success)
        {
            Toast.Success("TaskCreated");
            await OnRefreshRequested.InvokeAsync();
        }
        else
            Toast.Error("Error", "SetupFailed");
    }

    private async Task OpenSetupDialogAsync()
    {
        if (BlockedByOfflineAgent()) return;
        var result = await Dialog.OpenAsync<ServerSetupDialog>(
            L["RkhunterSetup"].Value,
            new Dictionary<string, object?>
            {
                { "Kind", ServerSetupKind.Rkhunter },
                { "Model", new ServerSetupDialogModel { MailOnWarning = _setupMailOnWarning } }
            },
            new OmniDialogOptions { Width = "32rem", AutoFocusFirstElement = false });
        if (result is not ServerSetupDialogModel model) return;
        _setupMailOnWarning = model.MailOnWarning;
        await SetupAsync();
    }

    private async Task GetLogsAsync()
    {
        if (BlockedByOfflineAgent()) return;
        var success = await Api.Security.GetRkhunterLogsAsync(ServerId, new RkhunterLogRequest());
        if (success)
            Toast.Success("TaskCreated");
    }

    private async Task LoadWarningsAsync()
    {
        _warnings = await Api.Security.GetRkhunterWarningsAsync(ServerId);
    }

    private async Task LoadScanHistoryAsync()
    {
        _scanHistory = await Api.Security.GetRkhunterScanHistoryAsync(ServerId);
    }

    private async Task LoadResultsAsync()
    {
        await LoadScanHistoryAsync();
        await LoadWarningsAsync();
    }

    /// <summary>
    /// Recette R-227: the heartbeat and task-completion reloads tell both grids before their new lists
    /// arrive, so the warnings and scans they did not hold read bold. Nothing re-renders the grids between
    /// this call and the assignment of the lists, which is the change they compare against.
    /// </summary>
    private async Task MarkLiveReloadAsync()
    {
        if (_warningsGrid is not null) await _warningsGrid.RefreshAsync();
        if (_historyGrid is not null) await _historyGrid.RefreshAsync();
    }

    private async Task ReloadFromPushAsync()
    {
        if (!Rk.IsInstalled) return;
        try
        {
            await MarkLiveReloadAsync();
            await LoadResultsAsync();
        }
        catch (HttpRequestException)
        {
            return; // the last results stay on screen; the next heartbeat retries
        }
        StateHasChanged();
    }

    /// <summary>
    /// Item #10.3 - hook for ServerDetail's TaskCompleted dispatcher. Refresh scan history +
    /// warnings whenever a RKHunter task completes so the UI lands on fresh data without the
    /// user manually hitting "Refresh".
    /// </summary>
    public async Task HandleTaskCompletedAsync(TaskCompletedNotification notification)
    {
        if (!notification.TaskName.Contains("RKHunter", StringComparison.OrdinalIgnoreCase))
            return;
        await MarkLiveReloadAsync();
        await LoadResultsAsync();
        await InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        if (_liveFeed is not null)
            await _liveFeed.DisposeAsync();
    }

    private async Task SaveScheduleAsync()
    {
        _savingSchedule = true;
        try
        {
            var cron = string.IsNullOrWhiteSpace(_scheduleCron) ? null : _scheduleCron.Trim();
            var success = await Api.Security.SetRkhunterScheduleAsync(ServerId, new RkhunterScheduleRequest { CronExpression = cron });
            if (success)
            {
                _scheduleCron = cron ?? string.Empty;
                _scheduleDirty = false;
                Toast.Success("Saved");
                await OnRefreshRequested.InvokeAsync();
            }
            else
                Toast.Error("Error", "SaveFailed");
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _savingSchedule = false;
        }
    }

    private static string GetScanStatusIcon(string status) => status switch
    {
        "clean" => "check_circle",
        "warning" => "warning",
        _ => "help"
    };

    private static string GetScanStatusColor(string status) => status switch
    {
        "clean" => "scan-status-clean",
        "warning" => "scan-status-warning",
        _ => "scan-status-unknown"
    };

    private static OmniTone GetScanBadgeStyle(string status) => status switch
    {
        "clean" => OmniTone.Success,
        "warning" => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    private string LocalizeScanStatus(string status) => status.ToLowerInvariant() switch
    {
        "clean" => L["RkhunterStatusClean"],
        "warning" => L["Warning"],
        "running" => L["Running"],
        "failed" => L["Failed"],
        _ => string.Format(L["RkhunterStatusUnknown"], status)
    };
}
