// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerApacheSection : ServerActionSectionBase, IDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    private string _moduleSearch = string.Empty;
    private string _vhostSearch = string.Empty;

    // Logs
    private string _logType = "error";
    private int _logLines = 100;
    private string? _logContent;
    private bool _logLoading;
    private bool _logFollowing;
    private System.Threading.Timer? _followTimer;
    private System.Threading.Timer? _configTimeoutTimer;

    // Config editor - the form now lives in ApacheConfigEditorDialog (OmniDialogService). The
    // shared model bridges the SignalR-pushed config text (HandleTaskCompleted) into the open
    // dialog. Non-null while a config-editor dialog is open so SignalR pushes can reach it.
    private ApacheConfigEditorModel? _configEditor;

    private ApacheDataDto Apache => Server.Apache;

    // Each heartbeat brings new Apache data; sites and modules that were not there read bold.
    private OmniDataGrid<ApacheVirtualHostGroup>? _enabledGrid;
    private OmniDataGrid<ApacheVirtualHostGroup>? _disabledGrid;
    private OmniDataGrid<ApacheModuleDto>? _modulesGrid;
    private readonly LiveGridRows _enabledRows = new();
    private readonly LiveGridRows _disabledRows = new();
    private readonly LiveGridRows _moduleRows = new();

    protected override async Task OnParametersSetAsync()
    {
        await _enabledRows.ObserveAsync(_enabledGrid, Server.Apache, ServerId);
        await _disabledRows.ObserveAsync(_disabledGrid, Server.Apache, ServerId);
        await _moduleRows.ObserveAsync(_modulesGrid, Server.Apache, ServerId);
    }

    private List<ApacheModuleDto> FilteredModules => string.IsNullOrWhiteSpace(_moduleSearch)
        ? Apache.Modules
        : Apache.Modules.Where(m => m.Name.Contains(_moduleSearch, StringComparison.OrdinalIgnoreCase)).ToList();

    private List<ApacheVirtualHostDto> FilteredVirtualHosts => string.IsNullOrWhiteSpace(_vhostSearch)
        ? Apache.VirtualHosts
        : Apache.VirtualHosts.Where(v =>
            v.ServerName.Contains(_vhostSearch, StringComparison.OrdinalIgnoreCase) ||
            v.DocumentRoot.Contains(_vhostSearch, StringComparison.OrdinalIgnoreCase)).ToList();

    private List<ApacheVirtualHostDto> EnabledVirtualHosts => FilteredVirtualHosts.Where(v => v.IsEnabled).ToList();
    private List<ApacheVirtualHostDto> DisabledVirtualHosts => FilteredVirtualHosts.Where(v => !v.IsEnabled).ToList();

    // Item #5.1: group vhosts by ServerName so a domain with separate :80 and :443 entries
    // surfaces as ONE row with two port chips, instead of two duplicate-looking rows. The
    // grouping is case-insensitive (Apache itself is case-insensitive on ServerName).
    internal List<ApacheVirtualHostGroup> EnabledVirtualHostGroups => GroupBy(EnabledVirtualHosts);
    internal List<ApacheVirtualHostGroup> DisabledVirtualHostGroups => GroupBy(DisabledVirtualHosts);

    private static List<ApacheVirtualHostGroup> GroupBy(IEnumerable<ApacheVirtualHostDto> source) =>
        source
            .GroupBy(v => string.IsNullOrEmpty(v.ServerName) ? v.ConfigFile : v.ServerName,
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => new ApacheVirtualHostGroup(
                ServerName: g.First().ServerName,
                Members: g.OrderBy(v => v.Port).ToList()))
            .OrderBy(g => g.ServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Synthetic row for the grouped sites table. Members are ordered by port asc so
    /// the chips render predictably (80 then 443 then anything else).</summary>
    internal sealed record ApacheVirtualHostGroup(string ServerName, List<ApacheVirtualHostDto> Members)
    {
        /// <summary>Primary config file - picked as the lowest-port member's. The two members
        /// of a domain usually share a single .conf, so editing the primary is the common case;
        /// when they diverge, the UI can offer a per-port action.</summary>
        public ApacheVirtualHostDto Primary => Members[0];

        /// <summary>Comma-separated DocumentRoot list when members disagree, else the single value.</summary>
        public string DocumentRootsCompact =>
            string.Join(", ",
                Members.Select(m => m.DocumentRoot)
                       .Where(d => !string.IsNullOrWhiteSpace(d))
                       .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private List<string> ApacheResourceNames => Apache.VirtualHosts.Select(v => v.ServerName).ToList();

    private async Task ExecuteActionAsync(ApacheAction action, string? targetName = null)
    {
        await ExecuteServerActionAsync(() => Api.ServerTools.ExecuteApacheActionAsync(
            ServerId, new ApacheActionRequest
            {
                Action = action,
                TargetName = targetName
            }), FollowTitle(action, targetName));
    }

    // Recette R3-37: what the follower calls the task this action queues.
    private string FollowTitle(ApacheAction action, string? targetName) => action switch
    {
        ApacheAction.EnableSite or ApacheAction.EnableModule => $"{L["Enable"]} {targetName}",
        ApacheAction.DisableSite or ApacheAction.DisableModule => $"{L["Disable"]} {targetName}",
        _ => L[action.ToString()]
    };

    private async Task FetchLogsAsync()
    {
        // SITE-OFFLINE-001: reading the logs queues an agent task too.
        if (BlockedByOfflineAgent()) return;
        _logLoading = true;
        _logContent = null;
        _follower?.Expect(L["ViewLogs"]);
        try
        {
            var success = await Api.ServerTools.GetApacheLogsAsync(ServerId, new ApacheLogRequest
            {
                LogType = _logType,
                Lines = _logLines
            });
            if (success)
                _logContent = L["TaskQueued"];
            else
            {
                _follower?.Forget();
                Toast.Error(L["ActionFailed"]);
            }
        }
        catch
        {
            _follower?.Forget();
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _logLoading = false;
        }
    }

    private async Task OpenConfigEditorAsync(string siteName)
    {
        if (BlockedByOfflineAgent()) return;
        var model = new ApacheConfigEditorModel { SiteName = siteName, Loading = true };
        _configEditor = model;

        // The actual config text is fetched on the agent and pushed back over SignalR
        // (see HandleTaskCompleted, which writes into the shared model and raises OnChanged).
        // The API call below only queues that task, so the dialog shows a loading indicator
        // until the content lands. If the queue request fails, stop the spinner and surface
        // the error rather than leaving the editor empty.
        try
        {
            var status = await Api.ServerTools.GetApacheVHostConfigAsync(ServerId, siteName);
            if (!status.Success)
            {
                model.Loading = false;
                model.NotifyChanged();
                Toast.Error(L["ActionFailed"]);
            }
            else
            {
                StartConfigTimeout(model);
            }
        }
        catch
        {
            model.Loading = false;
            model.NotifyChanged();
            Toast.Error(L["ActionFailed"]);
        }

        // Open the dialog (un-awaited here only conceptually - OpenAsync awaits the user's
        // Save/Cancel). The dialog returns an ApacheVHostSaveRequest on Save.
        var result = await Dialog.OpenAsync<ApacheConfigEditorDialog>(
            $"{L["EditConfig"]} - {siteName}",
            new Dictionary<string, object?> { { "Model", model } },
            new OmniDialogOptions { Width = "50rem", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });

        CancelConfigTimeout();
        if (ReferenceEquals(_configEditor, model))
            _configEditor = null;

        if (result is ApacheVHostSaveRequest request)
            await SaveConfigAsync(request);
    }

    // S-UX-40: the config text is pushed back over SignalR; if the agent never answers, clear the
    // spinner after 30s and surface an error instead of leaving the editor spinning forever.
    private void StartConfigTimeout(ApacheConfigEditorModel model)
    {
        _configTimeoutTimer?.Dispose();
        _configTimeoutTimer = new System.Threading.Timer(
            _ => _ = InvokeAsync(() =>
            {
                if (!model.Loading) return;
                model.Loading = false;
                model.NotifyChanged();
                Toast.Error(L["ActionFailed"]);
            }),
            null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
    }

    private void CancelConfigTimeout()
    {
        _configTimeoutTimer?.Dispose();
        _configTimeoutTimer = null;
    }

    private async Task SaveConfigAsync(ApacheVHostSaveRequest request)
    {
        if (BlockedByOfflineAgent()) return;
        _follower?.Expect(L["Save"]);
        try
        {
            var success = await Api.ServerTools.SaveApacheVHostConfigAsync(ServerId, request);
            if (success)
                Toast.Success(L["ConfigSaved"]);
            else
            {
                _follower?.Forget();
                Toast.Error(L["ActionFailed"]);
            }
        }
        catch
        {
            _follower?.Forget();
            Toast.Error(L["ActionFailed"]);
        }
    }

    public void HandleTaskCompleted(TaskCompletedNotification notification) =>
        _ = InvokeAsync(() => ShowTaskOutputAsync(notification));

    // Recette R3-30/R3-31: the completion notification carries no output (it never did), so the text a
    // read task produced is read from its log; a failed task shows why instead of an empty editor.
    private async Task ShowTaskOutputAsync(TaskCompletedNotification notification)
    {
        var isLogs = notification.TaskName.Contains("Apache logs", StringComparison.OrdinalIgnoreCase);
        var isConfig = notification.TaskName.Contains("Apache config", StringComparison.OrdinalIgnoreCase)
            && !notification.TaskName.Contains("save", StringComparison.OrdinalIgnoreCase);
        if (!isLogs && !(isConfig && _configEditor is not null)) return;

        string output;
        try { output = await ServerTaskOutputLoader.ReadAsync(Api, notification.TaskId); }
        catch (HttpRequestException) { output = L["TaskOutputUnavailable"]; }
        var failed = notification.Status != TaskExecutionStatus.Success;


        if (isLogs)
        {
            _logContent = failed ? L["TaskFailed"] + "\n" + output : output;
            _logLoading = false;
            StateHasChanged();
        }
        else if (_configEditor is { } model)
        {
            model.Content = failed ? string.Empty : output;
            model.Error = failed ? output : null;
            model.Loading = false;
            CancelConfigTimeout();
            model.NotifyChanged();
        }
    }

    internal static OmniTone GetStatusBadge(bool isRunning) =>
        isRunning ? OmniTone.Success : OmniTone.Danger;

    private void ToggleFollow()
    {
        _logFollowing = !_logFollowing;
        if (_logFollowing)
        {
            _followTimer = new System.Threading.Timer(
                async _ => await InvokeAsync(async () =>
                {
                    // SITE-OFFLINE-001: an agent that went offline ends the follow instead of a refusal every 5 s.
                    if (AgentOffline)
                    {
                        StopFollow();
                        StateHasChanged();
                    }
                    else if (!_logLoading)
                    {
                        await FetchLogsAsync();
                    }
                }),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));
        }
        else
        {
            StopFollow();
        }
    }

    private void StopFollow()
    {
        _followTimer?.Dispose();
        _followTimer = null;
        _logFollowing = false;
    }

    public void Dispose()
    {
        _followTimer?.Dispose();
        _configTimeoutTimer?.Dispose();
    }

    /// <summary>The export writes the ports of the group's virtual hosts.</summary>
    private static readonly Func<ApacheVirtualHostGroup, object?> PortsExport = group =>
        string.Join(", ", group.Members.Select(member => member.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
