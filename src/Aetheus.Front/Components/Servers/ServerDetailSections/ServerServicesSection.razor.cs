// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerServicesSection : IAsyncDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private ILogger<ServerServicesSection> Logger { get; set; } = default!;
    [Inject] private TaskTrackerService LiveTasks { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;
    [Parameter] public int ServerId { get; set; }

    /// <summary>
    /// Raised when a service install/uninstall/lifecycle action targeting this server succeeds, so the
    /// host can force a server-detail refresh and reflect the new installed/running state immediately
    /// instead of waiting for the next ≤30 s heartbeat.
    /// </summary>
    [Parameter] public EventCallback OnServiceChanged { get; set; }

    // Install/uninstall route through the controlled-sudo package-manage operation, which the backend
    // refuses unless the agent reports the aetheus-package sudoers drop-in (derived capability).
    // Gate the UI on it so the buttons aren't live actions that always 400 with a generic error.
    internal bool CanManagePackages => Server.PackageManagementAvailable == true;

    // S-UX-OFFA: gate the grid's mutating actions when the agent is offline (a queued task would strand).
    private bool ServerOnline => Server.Status == ServerStatus.Online;

    // S-UX-INST: the apt-installed service whose install task is in flight; on success we offer to start it
    // (a freshly-installed service stays `dead` until an explicit start, e.g. inside the VPS-sim container).
    private string? _pendingStartAfterInstall;

    // Every action launched from this page, followed per service by its task id (a name suffix
    // collides when two operations target the same service).
    private readonly ServiceActionTracker _actions = new();
    internal ServiceActionTracker Actions => _actions;
    private Func<string, ServiceActionState?>? _actionStateOf;
    private Func<string, ServiceActionState?> ActionStateOf => _actionStateOf ??= service => _actions.Of(service, LiveTasks.Tasks);
    private IReadOnlyList<ServiceInfoDto>? _servicesSeen;
    private bool _followingLiveTasks;

    private bool _logsVisible;
    private string? _logsServiceName;
    private readonly ServiceLogBuffer _logBuffer = new();
    private string _logsContent = string.Empty;
    private bool _logsLoading;
    private int _logLines = 100;
    private bool _logFollow;
    private bool _renewingFollowSession;
    // R-116: a read the user asked for over lines already on screen (a new line count) veils them until the first new line.
    private bool _veilKeptLogs;
    private ServiceLogAutoRefresh? _logRefresh;
    private bool _logTaskPending;
    private int? _currentLogTaskId;
    private HubConnection? _logHub;
    private readonly SemaphoreSlim _logStreamGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int? _activeServerId;
    private bool _disposed;

    private static readonly int[] LineOptions = [50, 100, 200, 500, 1000];

    private IEnumerable<ServiceInfoDto> SyntheticMissingModules =>
        ServerServiceModuleCatalog.MissingFrom(Server.Services);

    // Tab 1: Manageable + has dedicated Aetheus module (Docker, Apache, Mail, …)
    internal IEnumerable<ServiceInfoDto> ModuleInstalled =>
        Server.Services.Where(s => s.IsManageable && s.IsInstalled && HasDedicatedModule(s.Name)).OrderBy(s => s.Name);

    private IEnumerable<ServiceInfoDto> ModuleNotInstalled =>
        Server.Services.Where(s => s.IsManageable && !s.IsInstalled && HasDedicatedModule(s.Name))
            .Concat(SyntheticMissingModules)
            .OrderBy(s => s.Name);

    // Tab 2: Manageable but no dedicated module (nginx, mysql, fail2ban, …)
    private IEnumerable<ServiceInfoDto> OtherInstalled =>
        Server.Services.Where(s => s.IsManageable && s.IsInstalled && !HasDedicatedModule(s.Name)).OrderBy(s => s.Name);

    private IEnumerable<ServiceInfoDto> OtherNotInstalled =>
        Server.Services.Where(s => s.IsManageable && !s.IsInstalled && !HasDedicatedModule(s.Name)).OrderBy(s => s.Name);

    // Tab 3: System services (not manageable, read-only)
    private IEnumerable<ServiceInfoDto> SystemServices =>
        Server.Services.Where(s => !s.IsManageable).OrderBy(s => s.Name);

    internal int ModuleCount =>
        Server.Services.Count(s => s.IsManageable && HasDedicatedModule(s.Name)) + SyntheticMissingModules.Count();
    internal int OtherCount => Server.Services.Count(s => s.IsManageable && !HasDedicatedModule(s.Name));
    internal int SystemCount => Server.Services.Count(s => !s.IsManageable);

    internal static bool HasDedicatedModule(string serviceName) =>
        ServerServiceModuleCatalog.Contains(serviceName);

    // Header filter texts, built once so the columns see the same delegates on every render.
    private Func<string, string>? _typeText;
    private Func<string, string> TypeText => _typeText ??= GridFilterText.ForEnum<ServiceType>(L);
    private Func<string, string>? _yesNoText;
    private Func<string, string> YesNoText => _yesNoText ??= GridFilterText.YesNo(L);
    // The system grid shows the same translated status as the manageable ones.
    private string StatusText(string status) =>
        ServiceStatusPresentation.LabelKey(status, isRunning: false) is { } key ? L[key] : status;

    // Each heartbeat brings a new service list; system services that were not there read bold.
    private OmniDataGrid<ServiceInfoDto>? _systemGrid;
    private readonly LiveGridRows _systemRows = new();

    protected override async Task OnParametersSetAsync()
    {
        if (!_followingLiveTasks)
        {
            // A queued action turns "running" when the agent starts its task: the top bar's live list says so.
            LiveTasks.OnChanged += OnLiveTasksChanged;
            _followingLiveTasks = true;
        }
        // A new service list is the state the last successes produced: their "succeeded" line has
        // served once it has been read.
        if (_servicesSeen is not null && !ReferenceEquals(_servicesSeen, Server.Services))
            _actions.ForgetSucceeded(Clock.GetUtcNow());
        _servicesSeen = Server.Services;
        await _systemRows.ObserveAsync(_systemGrid, Server.Services, ServerId);
        if (_activeServerId == ServerId) return;
        await _logStreamGate.WaitAsync();
        try
        {
            await LeaveCurrentLogGroupCoreAsync();
            ResetTransientState();
            _activeServerId = ServerId;
        }
        finally
        {
            _logStreamGate.Release();
        }
    }

    public void HandleTaskCompleted(TaskCompletedNotification notification)
    {
        if (notification.ServerId != ServerId) return;
        // Correlate by task id: matching on the "- {service}" name suffix fired on the wrong
        // operation when an earlier task for the same service finished first.
        var finishedService = _actions.Complete(notification, Clock.GetUtcNow());
        var matchesBusy = finishedService is not null;
        if (matchesBusy)
        {
            var pendingStart = string.Equals(_pendingStartAfterInstall, finishedService, StringComparison.OrdinalIgnoreCase)
                ? _pendingStartAfterInstall
                : null;
            if (pendingStart is not null) _pendingStartAfterInstall = null;

            // Surface the outcome immediately instead of leaving the row spinning until the next
            // heartbeat - the agent's task result tells us whether the operation actually succeeded.
            // Use Notify(severity, titleKey, rawMessage): the task name is a runtime string, not a
            // resx key, so it must NOT be run through the localizer (the *Key overloads localize both).
            if (notification.Status == TaskExecutionStatus.Success)
            {
                Toast.Notify(OmniSeverity.Success, "TaskSucceeded", notification.TaskName);
                // Refresh the installed/running state now rather than waiting for the next heartbeat.
                _ = OnServiceChanged.InvokeAsync();
                // S-UX-INST: a just-installed service is stopped; offer to start it right away.
                if (pendingStart is { } justInstalled)
                    _ = PromptStartAfterInstallAsync(justInstalled);
            }
            else
            {
                var detail = notification.ExitCode is { } code
                    ? $"{notification.TaskName} (exit {code})"
                    : notification.TaskName;
                Toast.Notify(OmniSeverity.Danger, "TaskFailed", detail);
            }
        }

        var matchesLog = _currentLogTaskId == notification.TaskId;
        if (matchesLog)
        {
            // Recette R3-44: the notification carries no output, so the log a task read is fetched from
            // the task's own log; the pane stays busy until it arrives.
            _ = InvokeAsync(() => ShowLogOutputAsync(notification));
        }

        // Fired from a SignalR callback (off the render loop) - re-render so the cleared busy
        // spinner / refreshed log pane actually appear without waiting for another interaction.
        if (matchesBusy || matchesLog)
            _ = InvokeAsync(StateHasChanged);
    }

    private async Task ShowLogOutputAsync(TaskCompletedNotification notification)
    {
        string output;
        try { output = await ServerTaskOutputLoader.ReadAsync(Api, notification.TaskId); }
        catch (HttpRequestException) { output = L["TaskOutputUnavailable"]; }
        if (notification.Status != TaskExecutionStatus.Success)
            output = L["TaskFailed"] + "\n" + output;
        _logsContent = string.IsNullOrWhiteSpace(output) ? L["NoLogsAvailable"] : output;
        _logsLoading = false;
        _logTaskPending = false;
        StateHasChanged();
    }

    internal void TrackPendingInstall(string serviceName, int taskId)
    {
        _actions.Track(serviceName, taskId, "Install");
        _pendingStartAfterInstall = serviceName;
    }

    private void OnLiveTasksChanged() => _ = InvokeAsync(StateHasChanged);

    private void OpenServiceModule(string serviceName)
    {
        // Item #1: switched to real routes (/servers/{id}/{section}) instead of the legacy
        // ?section= query string.
        if (ServerServiceModuleCatalog.TryGetSection(serviceName, out var section))
            Nav.NavigateTo($"/servers/{ServerId}/{section}");
    }

    private async Task ConfirmAndExecuteAsync(ServiceAction action, string serviceName)
    {
        var msg = string.Format(L["ConfirmServiceAction"].Value, L[action.ToString()].Value, serviceName);
        // STD-BTN: stopping a service is destructive, so its confirmation is red and says "Arrêter".
        var destructive = action == ServiceAction.Stop;
        var confirmed = await Dialog.Confirm(msg, L["Confirm"].Value,
            ConfirmOptions(destructive, L[action.ToString()].Value, OmniIconName.Stop));
        if (confirmed != true) return;

        await Veil.RunAsync(() => ExecuteActionAsync(action, serviceName));
    }

    // Bulk bar: apply one action (Start/Stop/Restart/Install/Uninstall) to every checked service after a
    // single batch confirmation. Each service still becomes its own task on the agent; logs are excluded
    // (no bulk log view). Protected/unsupported targets just don't queue (reflected in the X/Y count).
    private async Task BulkActionAsync(BulkServiceRequest req)
    {
        if (req.Services.Count == 0) return;

        var verb = L[req.Kind].Value;
        var msg = string.Format(L["ConfirmBulkServiceAction"].Value, verb, req.Services.Count);
        // STD-BTN: a bulk stop or uninstall is destructive, so its confirmation is red and names the verb.
        var destructive = req.Kind is "Stop" or "Uninstall";
        var confirmed = await Dialog.Confirm(msg, L["Confirm"].Value, ConfirmOptions(destructive, verb));
        if (confirmed != true) return;
        if (!EnsureAgentOnline()) return;

        await Veil.RunAsync(async () =>
        {
            var queued = 0;
            var coordinator = new ServerBulkServiceActionCoordinator(Api, ServerId);
            foreach (var svc in req.Services)
            {
                var outcome = await coordinator.QueueAsync(
                    req.Kind, svc, ManageableServiceGrid.IsModuleInstallable(svc));
                if (!outcome.Queued) continue;
                queued++;
                // Each service of the batch is followed on its own row, like a single action.
                if (outcome.TaskId is { } taskId) _actions.Track(svc, taskId, req.Kind);
            }

            var summary = string.Format(L["BulkServiceQueued"].Value, queued, req.Services.Count);
            if (queued == 0)
                Toast.Error(L["Error"], summary);
            else
                Toast.Success(L["TaskQueued"], summary);

            await OnServiceChanged.InvokeAsync();
        });
    }

    // Recette R3-37: what became of the task an action of this page launched.
    private ServerTaskFollower? _follower;

    private async Task ExecuteActionAsync(ServiceAction action, string serviceName)
    {
        var taskId = await Api.Auth.ExecuteServiceActionAsync(ServerId, new ServiceActionRequest
        {
            Action = action,
            ServiceName = serviceName
        });

        if (taskId is not null)
        {
            _actions.Track(serviceName, taskId.Value, action.ToString());
            _follower?.Follow($"{action} - {serviceName}", taskId.Value);
            Toast.Success(L["TaskQueued"], $"{action} - {serviceName}");
        }
        else
        {
            Toast.Error(L["Error"], $"{action} - {serviceName}");
        }
    }

    // The agent must be online to claim and run an install/uninstall task; queuing one for an offline
    // server would just strand it Pending (the backend rejects it too). Block early with a clear toast.
    private bool EnsureAgentOnline()
    {
        if (Server.Status == ServerStatus.Online) return true;
        Toast.Error(L["TaskAgentOffline"], L["AgentOfflineActionBlocked"]);
        return false;
    }

    private async Task InstallServiceAsync(string serviceName)
    {
        if (!EnsureAgentOnline()) return;

        var msg = string.Format(L["ConfirmInstallService"].Value, serviceName);
        var confirmed = await Dialog.Confirm(msg, L["Confirm"].Value,
            new OmniConfirmOptions { OkButtonText = L["Install"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        // TeamSpeak installs through its dedicated module helper (the typed TeamspeakSetup operation),
        // not apt - route it to the teamspeak setup endpoint with the documented defaults.
        if (ManageableServiceGrid.IsModuleInstallable(serviceName))
        {
            var ok = await Api.Teamspeak.SetupTeamspeakAsync(ServerId, new TeamspeakSetupRequest());
            if (ok)
            {
                Toast.Success(L["TaskQueued"], $"Install - {serviceName}");
                await OnServiceChanged.InvokeAsync();
            }
            else
            {
                Toast.Error(L["Error"], $"Install - {serviceName}");
            }
            return;
        }

        var taskId = await Api.Auth.InstallServiceAsync(ServerId, serviceName);

        if (taskId is not null)
        {
            TrackPendingInstall(serviceName, taskId.Value);   // S-UX-INST: offer to start it once the install lands
            _follower?.Follow($"Install - {serviceName}", taskId.Value);
            Toast.Success(L["TaskQueued"], $"Install - {serviceName}");
        }
        else
        {
            Toast.Error(L["Error"], $"Install - {serviceName}");
        }
    }

    // S-UX-INST: after a successful install, an apt service is present but not started. Offer a one-click
    // start rather than leaving the user to hunt for the Start button in the (now Installed) grid.
    private async Task PromptStartAfterInstallAsync(string serviceName)
    {
        if (Server.Status != ServerStatus.Online) return;
        var confirmed = await Dialog.Confirm(
            string.Format(L["ConfirmStartAfterInstall"].Value, serviceName),
            L["ServiceInstalledTitle"].Value,
            new OmniConfirmOptions { OkButtonText = L["Start"].Value, CancelButtonText = L["Later"].Value });
        if (confirmed == true)
            await ExecuteActionAsync(ServiceAction.Start, serviceName);
    }

    private async Task UninstallServiceAsync(string serviceName)
    {
        if (!EnsureAgentOnline()) return;

        var msg = string.Format(L["ConfirmUninstallService"].Value, serviceName);
        var confirmed = await Dialog.Confirm(msg, L["Confirm"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Uninstall"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Veil.RunAsync(async () =>
        {
            var taskId = await Api.Auth.UninstallServiceAsync(ServerId, serviceName);

            if (taskId is not null)
            {
                _actions.Track(serviceName, taskId.Value, "Uninstall");
                _follower?.Follow($"Uninstall - {serviceName}", taskId.Value);
                Toast.Success(L["TaskQueued"], $"Uninstall - {serviceName}");
            }
            else
            {
                Toast.Error(L["Error"], $"Uninstall - {serviceName}");
            }
        });
    }

    internal async Task ViewServiceLogsAsync(string serviceName)
    {
        _logsServiceName = serviceName;
        _logsVisible = true;
        // The viewer opens with auto-refresh on; the switch freezes it.
        _logFollow = true;
        await StartLogStreamAsync();
        ScheduleLogRefresh();
    }

    /// <summary>Reads the last lines again, current ones kept on screen; skipped while the previous read
    /// is pending, so a slow or offline agent never accumulates queued tasks.</summary>
    internal Task RefreshLogsTickAsync()
    {
        if (_disposed || !_logsVisible || !_logFollow || _logTaskPending) return Task.CompletedTask;
        _renewingFollowSession = true;
        return StartLogStreamAsync();
    }

    private void ScheduleLogRefresh()
    {
        _logRefresh ??= new ServiceLogAutoRefresh(() => InvokeAsync(RefreshLogsTickAsync));
        if (_disposed || !_logsVisible || !_logFollow) _logRefresh.Stop();
        else _logRefresh.Start();
    }

    private async Task StartLogStreamAsync()
    {
        var keepContentUntilFirstLine = _renewingFollowSession;
        var veilKeptContent = _veilKeptLogs;
        _veilKeptLogs = false;
        _renewingFollowSession = false;
        if (_logsServiceName is null || _disposed) return;
        try
        {
            await _logStreamGate.WaitAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (_logsServiceName is null || _disposed) return;
            var serverId = ServerId;
            var serviceName = _logsServiceName;
            await LeaveCurrentLogGroupCoreAsync();

            ResetLogPane(keepContentUntilFirstLine, veilKeptContent);

            var taskId = await Api.Security.RequestServiceLogsAsync(
                serverId, serviceName, _logLines, follow: false, _lifetimeCts.Token);
            if (_disposed || serverId != ServerId || serviceName != _logsServiceName)
                return;
            if (taskId is null)
            {
                Toast.Error(L["Error"], $"Logs - {serviceName}");
                _logsLoading = false;
                return;
            }

            _currentLogTaskId = taskId;
            _logTaskPending = true;
            if (!await EnsureLogHubAsync())
            {
                _logsLoading = false;
                return;
            }
            await JoinLogTaskGroupAsync(taskId.Value);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        finally
        {
            _logStreamGate.Release();
        }
    }

    // A renewed follow session keeps the lines on screen until the new session's first line replaces
    // them (it re-sends the last N lines), instead of flashing an empty pane.
    private void ResetLogPane(bool keepContentUntilFirstLine, bool veilKeptContent)
    {
        if (keepContentUntilFirstLine)
        {
            _logBuffer.ReplaceOnNextLine();
            if (veilKeptContent)
            {
                // The lines stay mounted under OE's busy veil until the first new line (or the task output) lands.
                _logsLoading = true;
                StateHasChanged();
            }
            return;
        }
        _logBuffer.Clear();
        _logsContent = string.Empty;
        _logsLoading = true;
        StateHasChanged();
    }

    // Follows the log task's lines; a failed join is logged, the page keeps working.
    private async Task JoinLogTaskGroupAsync(int taskId)
    {
        try
        {
            await _logHub!.InvokeAsync("JoinTaskGroup", taskId, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ServerServices] JoinTaskGroup failed");
        }
    }

    private async Task<bool> EnsureLogHubAsync()
    {
        if (_logHub is not null) return true;

        _logHub = HubFactory.Create("logs");
        _logHub.On<TaskLogDto>("LogReceived", log =>
        {
            if (log.TaskId != _currentLogTaskId) return;
            AppendLog(log.Message);
            _logsLoading = false;
            _ = InvokeAsync(StateHasChanged);
        });

        _logHub.On<List<TaskLogDto>>("LogsReceived", logs =>
        {
            var batch = logs.Where(l => l.TaskId == _currentLogTaskId).ToList();
            if (batch.Count == 0) return;
            foreach (var log in batch)
                AppendLog(log.Message);
            _logsLoading = false;
            _ = InvokeAsync(StateHasChanged);
        });

        // Group membership is per-connection and lost on auto-reconnect - re-join the current
        // task's log group so streaming continues after a transient drop.
        _logHub.RejoinOnReconnect(async () =>
        {
            if (_currentLogTaskId > 0)
                await _logHub.InvokeAsync("JoinTaskGroup", _currentLogTaskId);
        });

        try
        {
            await _logHub.StartAsync();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "[ServerServices] Log hub connection failed");
            await _logHub.DisposeAsync();
            _logHub = null;
            return false;
        }
    }

    private async Task LeaveCurrentLogGroupCoreAsync()
    {
        if (_logHub is null || _currentLogTaskId is null) return;
        try
        {
            await _logHub.InvokeAsync("LeaveTaskGroup", _currentLogTaskId);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "[ServerServices] LeaveTaskGroup failed"); }
        _currentLogTaskId = null;
    }

    private void AppendLog(string? message)
    {
        if (message is null) return;
        _logsContent = _logBuffer.Append(message);
    }

    // S-UX-M6T2: an actionable hint instead of journalctl's raw permission notice.
    internal bool HasInsufficientJournalPermissions =>
        ServiceLogAutoRefresh.ShowsInsufficientJournalPermissions(_logsContent);

    internal async Task OnLogLinesChanged(int lines)
    {
        _logLines = lines;
        _renewingFollowSession = true;
        _veilKeptLogs = true;
        await StartLogStreamAsync();
    }

    internal async Task OnFollowChangedAsync(bool follow)
    {
        _logFollow = follow;
        ScheduleLogRefresh();
        if (!follow) return;
        // A user switching it back on wants the current lines now, pending read or not.
        _renewingFollowSession = true;
        await StartLogStreamAsync();
    }

    internal bool IsFollowingLogs => _logFollow;

    private async Task CloseLogsAsync()
    {
        await _logStreamGate.WaitAsync();
        try
        {
            await LeaveCurrentLogGroupCoreAsync();
            ResetLogState();
        }
        finally
        {
            _logStreamGate.Release();
        }
    }

    private void ResetLogState()
    {
        _logRefresh?.Stop();
        _logTaskPending = false;
        _logsVisible = false;
        _logsServiceName = null;
        _currentLogTaskId = null;
        _logBuffer.Clear();
        _logsContent = string.Empty;
        _logsLoading = false;
        _logFollow = false;
        _logLines = 100;
    }

    private void ResetTransientState()
    {
        _pendingStartAfterInstall = null;
        _actions.Clear();
        ResetLogState();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_followingLiveTasks) LiveTasks.OnChanged -= OnLiveTasksChanged;
        _logRefresh?.Stop();
        _lifetimeCts.Cancel();
        await _logStreamGate.WaitAsync();
        try
        {
            await LeaveCurrentLogGroupCoreAsync();
            if (_logHub is not null)
                await _logHub.DisposeAsync();
        }
        finally
        {
            _logStreamGate.Release();
            _lifetimeCts.Dispose();
            _logStreamGate.Dispose();
        }
    }

    // Every confirmation names its verb : red when destructive, blue otherwise.
    private OmniConfirmOptions ConfirmOptions(bool destructive, string verb, OmniIconName? icon = null) => new()
    {
        Destructive = destructive,
        ConfirmIcon = destructive ? icon : null,
        OkButtonText = verb,
        CancelButtonText = L["GoBack"].Value
    };
}
