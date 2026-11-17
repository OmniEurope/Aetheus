// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerApacheSection : ServerActionSectionBase, IDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    // Logs
    private string _logType = "error";
    private int _logLines = 100;
    private string? _logContent;
    private string? _logDiagnostic;
    private bool _logLoading;
    private bool _logFollowing;
    private System.Threading.Timer? _followTimer;
    private System.Threading.Timer? _configTimeoutTimer;

    // Config editor - the form now lives in ApacheConfigEditorDialog (OmniDialogService). The
    // shared model bridges the SignalR-pushed config text (HandleTaskCompleted) into the open
    // dialog. Non-null while a config-editor dialog is open so SignalR pushes can reach it.
    private ApacheConfigEditorModel? _configEditor;

    private ApacheDataDto Apache => Server.Apache;

    // R-119: modules Debian's a2dismod refuses to disable (the essential list of /usr/sbin/a2enmod, Ubuntu 24.04).
    private static readonly HashSet<string> EssentialModules = new(StringComparer.Ordinal)
    {
        "alias", "auth_basic", "authn_file", "authz_host", "authz_user", "autoindex", "deflate", "dir", "env",
        "filter", "logio", "mime", "negotiation", "setenvif", "unixd", "version", "watchdog"
    };

    private static bool IsEssentialModule(ApacheModuleDto module) =>
        EssentialModules.Contains(module.Name.Replace("_module", ""));

    // Each heartbeat brings new Apache data; sites and modules that were not there read bold.
    private OmniDataGrid<ApacheVirtualHostGroup>? _enabledGrid;
    private OmniDataGrid<ApacheVirtualHostGroup>? _disabledGrid;
    private OmniDataGrid<ApacheModuleDto>? _modulesGrid;
    private readonly LiveGridRows _enabledRows = new();
    private readonly LiveGridRows _disabledRows = new();
    private readonly LiveGridRows _moduleRows = new();

    [Inject] private TimeProvider Clock { get; set; } = default!;

    /// <summary>The server pages' loader: the auto-refresh asks it to read the server's state again.</summary>
    [CascadingParameter(Name = "ServerLoader")] public ServerDetailLoader? Loader { get; set; }

    // Recette R-17: while the page is open its state and data are read again every 15 s, with no manual reload, so
    // an Apache restarted on the host shows. Skipped while a task of this page is in progress (its follower waits for the
    // server's next state), never overlapping, stopped with the page.
    private ServerPageAutoRefresh? _autoRefresh;

    protected override void OnInitialized() =>
        _autoRefresh = new ServerPageAutoRefresh(Clock, () => ActionBusy || (_follower?.Progressing ?? false), RefreshStateAsync);

    private Task RefreshStateAsync() =>
        Loader is null ? Task.CompletedTask : InvokeAsync(() => Loader.RefreshAsync(ServerId));

    protected override async Task OnParametersSetAsync()
    {
        await _enabledRows.ObserveAsync(_enabledGrid, Server.Apache, ServerId);
        await _disabledRows.ObserveAsync(_disabledGrid, Server.Apache, ServerId);
        await _moduleRows.ObserveAsync(_modulesGrid, Server.Apache, ServerId);
    }

    private List<ApacheVirtualHostDto> EnabledVirtualHosts => Apache.VirtualHosts.Where(v => v.IsEnabled).ToList();
    private List<ApacheVirtualHostDto> DisabledVirtualHosts => Apache.VirtualHosts.Where(v => !v.IsEnabled).ToList();

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
                ServerName: string.IsNullOrEmpty(g.First().ServerName) ? Path.GetFileName(g.First().ConfigFile) : g.First().ServerName,
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

        /// <summary>Recette R-13: the number of VirtualHost blocks carrying this server name, in its own column.</summary>
        public int BlockCount => Members.Count;

        /// <summary>Comma-separated DocumentRoot list when members disagree, else the single value.</summary>
        public string DocumentRootsCompact =>
            string.Join(", ",
                Members.Select(m => m.DocumentRoot)
                       .Where(d => !string.IsNullOrWhiteSpace(d))
                       .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private List<string> ApacheResourceNames => Apache.VirtualHosts.Select(v => v.ServerName).ToList();

    // R-37: no Apache action veils the page (restarting Apache is not a blocking task, see ServerBlockingTasks): the
    // follower shows its progress block. A confirmed action (stop Apache, delete a site) is followed by the follower.
    protected override bool VeilsConfirmedActionOnPage => false;

    internal async Task ExecuteActionAsync(ApacheAction action, string? targetName = null)
    {
        await ExecuteServerActionAsync(() => Api.ServerTools.ExecuteApacheActionAsync(
            ServerId, new ApacheActionRequest
            {
                Action = action,
                TargetName = targetName
            }), FollowTitle(action, targetName), changesInventory: action != ApacheAction.TestConfig);
    }

    // Recette R3-37: what the follower calls the task this action queues.
    private string FollowTitle(ApacheAction action, string? targetName) => action switch
    {
        ApacheAction.EnableSite or ApacheAction.EnableModule => $"{L["Enable"]} {targetName}",
        ApacheAction.DisableSite or ApacheAction.DisableModule => $"{L["Disable"]} {targetName}",
        ApacheAction.DeleteSite => $"{L["Delete"]} {targetName}",
        _ => L[action.ToString()]
    };

    /// <summary>Recette R-12: what a task adopted on arrival ("Apache EnableSite - a.conf") is called, as the page names
    /// the task it launches itself. A name this page does not know is left to the follower.</summary>
    internal string? AdoptedTitle(string taskName) => ServerAdoptedTaskTitles.Resolve(taskName,
    [
        ServerAdoptedTaskTitles.Key(L, "Apache logs - ", "ViewLogs"),
        ServerAdoptedTaskTitles.Key(L, "Apache save ", "Save"),
        new("Apache ", rest => ServerAdoptedTaskTitles.Action<ApacheAction>(rest, FollowTitle))
    ]);

    /// <summary>The distinct site files of a row (its :80 and :443 blocks often share one).</summary>
    internal static IReadOnlyList<string> SiteFiles(ApacheVirtualHostGroup group) =>
        group.Members.Select(member => Path.GetFileName(member.ConfigFile))
            .Where(file => !string.IsNullOrEmpty(file))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Recette R-15: one site file of a row with the ports its blocks serve ("80, 443").</summary>
    /// <param name="ForeignOwner">Recette R-14: the account that owns the file when it is not the agent's own, so Aetheus cannot
    /// delete it; null when the agent owns it or when the agent does not say (an older agent): no badge then.</param>
    internal sealed record SiteFileLine(string File, string Ports, string? ForeignOwner = null);

    /// <summary>The files behind a row, each with its ports, in the order of the row's blocks (lowest port first).</summary>
    internal static IReadOnlyList<SiteFileLine> SiteFileLines(ApacheVirtualHostGroup group) =>
        group.Members
            .Where(member => !string.IsNullOrEmpty(Path.GetFileName(member.ConfigFile)))
            .GroupBy(member => Path.GetFileName(member.ConfigFile), StringComparer.Ordinal)
            .Select(file => new SiteFileLine(file.Key,
                string.Join(", ", file.Select(member => member.Port.ToString(CultureInfo.InvariantCulture)).Distinct()),
                file.Select(member => member.ConfigFileForeignOwner).FirstOrDefault(owner => !string.IsNullOrWhiteSpace(owner))))
            .ToList();

    // Recette R-12: deleting a disabled site's file is destructive, so it is confirmed (red verb, "Revenir").
    private Task ConfirmDeleteSite(string file) =>
        ShowDestructiveConfirm(L["ApacheDeleteSiteTitle"], string.Format(L["ApacheDeleteSiteConfirm"], file),
            () => ExecuteActionAsync(ApacheAction.DeleteSite, file), L["Delete"].Value, OmniIconName.Delete);

    internal async Task FetchLogsAsync()
    {
        // SITE-OFFLINE-001: reading the logs queues an agent task too.
        if (BlockedByOfflineAgent()) return;
        _logLoading = true;
        _logContent = null;
        _logDiagnostic = null;
        // R-19: reading the logs is a one-off action, not a global operation: the progress block, no veil.
        _follower?.Expect(L["ViewLogs"], settle: false);
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

    /// <summary>How long the editor waits for the agent's answer (30 s); a test under a loaded runner sets it longer so the
    /// timeout cannot overtake the answer it is waiting for.</summary>
    internal TimeSpan ConfigReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
            null, ConfigReadTimeout, Timeout.InfiniteTimeSpan);
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

    /// <summary>The name the backend gives the task that reads a site's configuration (ApacheService.GetVHostConfigAsync);
    /// the queue request returns no task id, so this name is what ties an answer to the site that was asked.</summary>
    private static string ConfigReadTaskName(string siteName) => $"Apache config - {siteName}";

    public void HandleTaskCompleted(TaskCompletedNotification notification) =>
        _ = InvokeAsync(() => ShowTaskOutputAsync(notification));

    // Recette R3-30/R3-31: the completion notification carries no output (it never did), so the text a
    // read task produced is read from its log; a failed task shows why instead of an empty editor.
    private async Task ShowTaskOutputAsync(TaskCompletedNotification notification)
    {
        var isLogs = notification.TaskName.Contains("Apache logs", StringComparison.OrdinalIgnoreCase);
        // F-FRONT-025: the read answers the editor that is open now, for the site it asked for, and only while that
        // editor still waits: a late answer for another site, an editor already closed, answered or timed out is dropped.
        var editor = _configEditor;
        var isConfig = editor is { Loading: true }
            && string.Equals(notification.TaskName, ConfigReadTaskName(editor.SiteName), StringComparison.OrdinalIgnoreCase);
        if (!isLogs && !isConfig) return;

        string output;
        try { output = await ServerTaskOutputLoader.ReadAsync(Api, notification.TaskId); }
        catch (HttpRequestException) { output = L["TaskOutputUnavailable"]; }
        var failed = notification.Status != TaskExecutionStatus.Success;


        if (isLogs)
        {
            // R-19: a log the agent could not show is a localized sentence, never the agent's diagnostic line.
            _logDiagnostic = ApacheLogDiagnosticText.Describe(L, output);
            _logContent = _logDiagnostic is not null ? null : failed ? L["TaskFailed"] + "\n" + output : output;
            _logLoading = false;
            StateHasChanged();
        }
        else if (editor is { } model && ReferenceEquals(_configEditor, model) && model.Loading)
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
        _autoRefresh?.Dispose();
        _followTimer?.Dispose();
        _configTimeoutTimer?.Dispose();
    }

    /// <summary>The export writes the ports of the group's virtual hosts.</summary>
    private static readonly Func<ApacheVirtualHostGroup, object?> PortsExport = group =>
        string.Join(", ", group.Members.Select(member => member.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
