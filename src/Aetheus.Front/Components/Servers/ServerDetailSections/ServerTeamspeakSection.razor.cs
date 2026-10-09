// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerTeamspeakSection : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;
    [Parameter, EditorRequired] public int ServerId { get; set; }

    private bool _actionRunning;
    private TeamspeakDataCoordinator _data = default!;
    private TeamspeakOperationRunner _operations = default!;
    private AetheusDataGrid<TeamspeakClientDto>? _clientsGrid;
    private AetheusDataGrid<TeamspeakChannelDto>? _channelsGrid;
    private AetheusDataGrid<TeamspeakBanDto>? _bansGrid;
    private List<string> _platformValues = [];
    private int? _loadedServerId;
    // R-181: replaces the Refresh button and the former 30 s timer. Heartbeats only: TeamSpeak task
    // completions already reach HandleTaskCompletedAsync through the page's loader.
    private ServerLiveFeed? _liveFeed;
    internal ServerLiveFeed? LiveFeed => _liveFeed;

    private string _setupInstallPath = "/opt/teamspeak3-server_linux_amd64";
    private int _setupVoicePort = 9987;
    private int _setupQueryPort = 10011;

    private int _kickClientId;
    private string _kickNickname = string.Empty;
    private string _kickReason = string.Empty;

    private int _moveClientId;
    private string _moveNickname = string.Empty;
    private int _moveTargetChannelId;
    private string _moveChannelPassword = string.Empty;

    private int _pokeClientId;
    private string _pokeNickname = string.Empty;
    private string _pokeMessage = string.Empty;

    private int _gracefulRestartSeconds = 60;
    private string _gracefulRestartMessage = string.Empty;

    private string _banClientUniqueId = string.Empty;
    private string _banNickname = string.Empty;
    private string _banReason = string.Empty;
    private int _banDuration = 3600;

    private string _newChannelName = string.Empty;
    private int? _newChannelParentId;
    private string _newChannelPassword = string.Empty;
    private int? _newChannelMaxClients;
    private bool _newChannelPermanent = true;

    private int _editChannelId;
    private string _editChannelName = string.Empty;
    private string _editChannelPassword = string.Empty;
    private int? _editChannelMaxClients;

    private string _snapshotBlob = string.Empty;
    private string _snapshotConfirmation = string.Empty;

    private string _editServerName = string.Empty;
    private string _editServerPassword = string.Empty;
    private int? _editServerMaxClients;
    private string _editWelcomeMessage = string.Empty;

    private string _globalMessage = string.Empty;

    // Log lines
    private int _logLines = 100;

    private TeamspeakDataDto Ts => _data?.State ?? Server.Teamspeak;

    protected override void OnInitialized()
    {
        _data = new TeamspeakDataCoordinator(Api);
        _operations = new TeamspeakOperationRunner(Toast);
        _gracefulRestartMessage = L["TeamspeakGracefulRestartMessage"];
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId == ServerId) return;
        var serverChanged = _loadedServerId is not null;
        _loadedServerId = ServerId;
        ResetOperationTargets();
        _liveFeed ??= new ServerLiveFeed(HubFactory);
        _ = _liveFeed.StartAsync(ServerId, ServerLiveFeedTriggers.Heartbeat,
            () => InvokeAsync(RefreshLiveAsync));
        await _data.ResetAsync(ServerId, Server.Teamspeak, () => InvokeAsync(StateHasChanged));
        await LoadPlatformValuesAsync();
        // Recette R-327: the grids scroll (remote virtualization) and only show what they fetched
        // themselves, so another server reloads the ones already on screen from their first row.
        if (!serverChanged) return;
        if (_clientsGrid is not null) await _clientsGrid.Reload();
        if (_channelsGrid is not null) await _channelsGrid.Reload();
        if (_bansGrid is not null) await _bansGrid.Reload();
    }

    public async Task HandleTaskCompletedAsync(TaskCompletedNotification notification)
    {
        if (notification.ServerId != ServerId ||
            !notification.TaskName.StartsWith("TeamSpeak", StringComparison.OrdinalIgnoreCase))
            return;
        // The notification may arrive off the renderer's thread; the grids refresh on it.
        await InvokeAsync(RefreshLiveAsync);
    }

    // Recette R-226: a heartbeat or a TeamSpeak task is live data. The state is fetched again and each grid
    // on screen refreshes quietly through its own LoadData, so its page, sort and header filters are kept.
    private async Task RefreshLiveAsync()
    {
        await _data.RefreshStateAsync();
        await LoadPlatformValuesAsync();
        if (_clientsGrid is not null) await _clientsGrid.Refresh();
        if (_channelsGrid is not null) await _channelsGrid.Refresh();
        if (_bansGrid is not null) await _bansGrid.Refresh();
    }

    // Recette R-210: the platforms the clients grid's Platform header filter offers, across every client.
    private async Task LoadPlatformValuesAsync()
    {
        var serverId = ServerId;
        try
        {
            var values = await Api.Teamspeak.GetTeamspeakFilterValuesAsync(serverId);
            if (serverId == ServerId) _platformValues = values.Platforms;
        }
        catch (HttpRequestException)
        {
            if (serverId == ServerId) _platformValues = [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_liveFeed is not null)
            await _liveFeed.DisposeAsync();
        await _data.DisposeAsync();
    }

    private void ResetOperationTargets()
    {
        _actionRunning = false;
        _kickClientId = 0;
        _kickNickname = string.Empty;
        _kickReason = string.Empty;
        _moveClientId = 0;
        _moveNickname = string.Empty;
        _moveTargetChannelId = 0;
        _moveChannelPassword = string.Empty;
        _pokeClientId = 0;
        _pokeNickname = string.Empty;
        _pokeMessage = string.Empty;
        _banClientUniqueId = string.Empty;
        _banNickname = string.Empty;
        _banReason = string.Empty;
        _banDuration = 3600;
        _newChannelName = string.Empty;
        _newChannelParentId = null;
        _newChannelPassword = string.Empty;
        _newChannelMaxClients = null;
        _editChannelId = 0;
        _editChannelName = string.Empty;
        _editChannelPassword = string.Empty;
        _editChannelMaxClients = null;
        _snapshotBlob = string.Empty;
        _snapshotConfirmation = string.Empty;
        _editServerName = string.Empty;
        _editServerPassword = string.Empty;
        _editServerMaxClients = null;
        _editWelcomeMessage = string.Empty;
        _globalMessage = string.Empty;
    }

    private async Task ExecuteActionAsync(TeamspeakAction action)
    {
        _actionRunning = true;
        try
        {
            await _operations.RunOperationAsync(
                () => Api.Teamspeak.ExecuteTeamspeakActionAsync(ServerId, new TeamspeakActionRequest { Action = action }),
                action.ToString());
        }
        finally { _actionRunning = false; }
    }

    private async Task<TeamspeakDialogModel?> OpenDialogAsync(TeamspeakDialogMode mode, string title, TeamspeakDialogModel model, string width = "32rem")
    {
        var channels = mode is TeamspeakDialogMode.Move or TeamspeakDialogMode.CreateChannel
            ? await _data.LoadAllChannelsAsync()
            : _data.Channels;
        var result = await Dialog.OpenAsync<TeamspeakOperationDialog>(title,
            new Dictionary<string, object?>
            {
                { "Mode", mode }, { "Model", model }, { "Channels", channels }, { "ServerName", Ts.ServerName }
            },
            new OmniDialogOptions { Width = width, AutoFocusFirstElement = false });
        return result as TeamspeakDialogModel;
    }

    private async Task OpenSetupDialogAsync()
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.Setup, L["TeamspeakSetup"], new TeamspeakDialogModel
        {
            InstallPath = _setupInstallPath,
            VoicePort = _setupVoicePort,
            QueryPort = _setupQueryPort
        });
        if (result is null) return;
        _setupInstallPath = result.InstallPath;
        _setupVoicePort = result.VoicePort;
        _setupQueryPort = result.QueryPort;
        await SetupAsync();
    }

    private async Task OpenGracefulRestartDialogAsync()
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.GracefulRestart, L["GracefulRestart"], new TeamspeakDialogModel
        {
            WarningSeconds = _gracefulRestartSeconds,
            Message = _gracefulRestartMessage
        });
        if (result is null) return;
        _gracefulRestartSeconds = result.WarningSeconds;
        _gracefulRestartMessage = result.Message;
        await GracefulRestartAsync();
    }

    private async Task OpenSnapshotDeployDialogAsync()
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.SnapshotDeploy, L["SnapshotDeploy"], new TeamspeakDialogModel(), "38rem");
        if (result is null) return;
        _snapshotBlob = result.SnapshotBlob;
        _snapshotConfirmation = result.Confirmation;
        await DeploySnapshotAsync();
    }

    private async Task OpenCreateChannelDialogAsync()
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.CreateChannel, L["CreateChannel"], new TeamspeakDialogModel { IsPermanent = true });
        if (result is null) return;
        _newChannelName = result.Name;
        _newChannelParentId = result.ParentId;
        _newChannelPassword = result.Password;
        _newChannelMaxClients = result.MaxClients;
        _newChannelPermanent = result.IsPermanent;
        await CreateChannelAsync();
    }

    private async Task OpenEditServerDialogAsync()
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.EditServer, L["EditServer"], new TeamspeakDialogModel
        {
            Name = Ts.ServerName,
            MaxClients = Ts.MaxClients > 0 ? Ts.MaxClients : null
        });
        if (result is null) return;
        _editServerName = result.Name;
        _editServerPassword = result.Password;
        _editServerMaxClients = result.MaxClients;
        _editWelcomeMessage = result.Message;
        await EditServerAsync();
    }

    private async Task OpenMessageDialogAsync()
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.Message, L["SendMessage"], new TeamspeakDialogModel());
        if (result is null) return;
        _globalMessage = result.Message;
        await SendMessageAsync();
    }

    private async Task SetupAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.SetupTeamspeakAsync(ServerId, new TeamspeakSetupRequest
            {
                InstallPath = _setupInstallPath,
                VoicePort = _setupVoicePort,
                QueryPort = _setupQueryPort
            }),
            L["TeamspeakSetup"]);
    }

    private async Task ShowKickDialog(TeamspeakClientDto client)
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.Kick, L["KickClient"], new TeamspeakDialogModel { ClientId = client.ClientId, Nickname = client.Nickname });
        if (result is null) return;
        _kickClientId = result.ClientId;
        _kickNickname = result.Nickname;
        _kickReason = result.Reason;
        await KickAsync();
    }

    private async Task KickAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.KickTeamspeakClientAsync(ServerId, new TeamspeakKickRequest
            {
                ClientId = _kickClientId,
                ReasonMessage = _kickReason
            }),
            L["KickClient"]);
    }

    // Item #9 tier-1: relocate a client. Default target = client's current channel (so the
    // dropdown can show ALL channels including the current one and skip the no-op silently).
    private async Task ShowMoveDialog(TeamspeakClientDto client)
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.Move, L["MoveClient"], new TeamspeakDialogModel { ClientId = client.ClientId, Nickname = client.Nickname, TargetChannelId = client.ChannelId });
        if (result is null) return;
        _moveClientId = result.ClientId;
        _moveNickname = result.Nickname;
        _moveTargetChannelId = result.TargetChannelId;
        _moveChannelPassword = result.Password;
        await MoveAsync();
    }

    private async Task MoveAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.MoveTeamspeakClientAsync(ServerId, new TeamspeakMoveClientRequest
            {
                ClientId = _moveClientId,
                TargetChannelId = _moveTargetChannelId,
                ChannelPassword = string.IsNullOrEmpty(_moveChannelPassword) ? null : _moveChannelPassword
            }),
            L["MoveClient"]);
    }

    // Item #9 tier-1: send a single-client popup (poke). Distinct from the server-wide
    // "Send message" which broadcasts to every connected client.
    private async Task ShowPokeDialog(TeamspeakClientDto client)
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.Poke, L["PokeClient"], new TeamspeakDialogModel { ClientId = client.ClientId, Nickname = client.Nickname });
        if (result is null) return;
        _pokeClientId = result.ClientId;
        _pokeNickname = result.Nickname;
        _pokeMessage = result.Message;
        await PokeAsync();
    }

    private async Task PokeAsync()
    {
        if (string.IsNullOrWhiteSpace(_pokeMessage)) return;
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.PokeTeamspeakClientAsync(ServerId, new TeamspeakPokeClientRequest
            {
                ClientId = _pokeClientId,
                Message = _pokeMessage
            }),
            L["PokeClient"]);
    }

    // ===== Item #9 tier-2/3 - fire-and-forget ops that surface their result in the task tracker. =====

    private async Task FetchClientInfoAsync(TeamspeakClientDto client)
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.GetTeamspeakClientInfoAsync(
                ServerId, new TeamspeakClientInfoRequest { ClientId = client.ClientId }),
            $"{L["ClientInfo"]} - {client.Nickname}");
    }

    private async Task GracefulRestartAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.TeamspeakGracefulRestartAsync(ServerId, new TeamspeakGracefulRestartRequest
            {
                WarningSeconds = _gracefulRestartSeconds,
                WarningMessage = _gracefulRestartMessage
            }),
            L["GracefulRestart"]);
    }

    private async Task FetchServerInfoAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.GetTeamspeakServerInfoAsync(ServerId),
            L["TeamspeakStats"]);
    }

    private async Task CreateSnapshotAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.CreateTeamspeakSnapshotAsync(ServerId, new TeamspeakSnapshotCreateRequest()),
            L["SnapshotCreate"]);
    }

    private async Task DeploySnapshotAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.DeployTeamspeakSnapshotAsync(ServerId, new TeamspeakSnapshotDeployRequest
            {
                SnapshotBlob = _snapshotBlob,
                Confirmation = _snapshotConfirmation
            }),
            L["SnapshotDeploy"],
            () =>
            {
                _snapshotBlob = string.Empty;
                _snapshotConfirmation = string.Empty;
            });
    }

    private async Task ListServerGroupsAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.ListTeamspeakServerGroupsAsync(ServerId),
            L["TeamspeakServerGroups"]);
    }

    private async Task ListTokensAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.ListTeamspeakTokensAsync(ServerId),
            L["TeamspeakTokens"]);
    }

    private async Task ListComplaintsAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.ListTeamspeakComplaintsAsync(ServerId),
            L["TeamspeakComplaints"]);
    }

    private async Task ShowBanDialog(TeamspeakClientDto client)
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.Ban, L["BanClient"], new TeamspeakDialogModel { ClientUniqueId = client.UniqueId, Nickname = client.Nickname, Duration = 3600 });
        if (result is null) return;
        _banClientUniqueId = result.ClientUniqueId;
        _banNickname = result.Nickname;
        _banReason = result.Reason;
        _banDuration = result.Duration;
        await BanAsync();
    }

    private async Task BanAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.BanTeamspeakClientAsync(ServerId, new TeamspeakBanRequest
            {
                ClientUniqueId = _banClientUniqueId,
                DurationSeconds = _banDuration,
                Reason = _banReason
            }),
            L["BanClient"]);
    }

    private async Task LoadLogsAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.GetTeamspeakLogsAsync(ServerId, new TeamspeakLogRequest { Lines = _logLines }),
            L["Logs"]);
    }

    private async Task CreateChannelAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.CreateTeamspeakChannelAsync(ServerId, new TeamspeakCreateChannelRequest
            {
                Name = _newChannelName,
                ParentId = _newChannelParentId,
                Password = string.IsNullOrWhiteSpace(_newChannelPassword) ? null : _newChannelPassword,
                MaxClients = _newChannelMaxClients,
                IsPermanent = _newChannelPermanent
            }),
            L["CreateChannel"],
            () =>
            {
                _newChannelName = string.Empty;
                _newChannelPassword = string.Empty;
                _newChannelParentId = null;
                _newChannelMaxClients = null;
            });
    }

    private async Task ShowEditChannelDialog(TeamspeakChannelDto ch)
    {
        var result = await OpenDialogAsync(TeamspeakDialogMode.EditChannel, L["EditChannel"], new TeamspeakDialogModel { ChannelId = ch.Id, Name = ch.Name, MaxClients = ch.MaxClients == -1 ? null : ch.MaxClients });
        if (result is null) return;
        _editChannelId = result.ChannelId;
        _editChannelName = result.Name;
        _editChannelPassword = result.Password;
        _editChannelMaxClients = result.MaxClients;
        await EditChannelAsync();
    }

    private async Task EditChannelAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.EditTeamspeakChannelAsync(ServerId, _editChannelId, new TeamspeakEditChannelRequest
            {
                ChannelId = _editChannelId,
                Name = _editChannelName,
                Password = string.IsNullOrWhiteSpace(_editChannelPassword) ? null : _editChannelPassword,
                MaxClients = _editChannelMaxClients
            }),
            L["EditChannel"]);
    }

    private async Task ShowDeleteChannelConfirm(TeamspeakChannelDto ch)
    {
        var confirmed = await Dialog.Confirm(string.Format(L["ConfirmDeleteChannel"], ch.Name), L["DeleteChannel"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed == true)
        {
            await _operations.RunOperationAsync(
                () => Api.Teamspeak.DeleteTeamspeakChannelAsync(ServerId, ch.Id),
                L["DeleteChannel"]);
        }
    }

    private async Task EditServerAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.EditTeamspeakServerAsync(ServerId, new TeamspeakServerEditRequest
            {
                ServerName = string.IsNullOrWhiteSpace(_editServerName) ? null : _editServerName,
                Password = string.IsNullOrWhiteSpace(_editServerPassword) ? null : _editServerPassword,
                MaxClients = _editServerMaxClients,
                WelcomeMessage = string.IsNullOrWhiteSpace(_editWelcomeMessage) ? null : _editWelcomeMessage
            }),
            L["EditServer"]);
    }

    private async Task SendMessageAsync()
    {
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.SendTeamspeakGlobalMessageAsync(ServerId, new TeamspeakGlobalMessageRequest
            {
                Message = _globalMessage
            }),
            L["SendMessage"],
            () => _globalMessage = string.Empty);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        await Js.InvokeVoidAsync("navigator.clipboard.writeText", text);
        Toast.Success(L["CopiedToClipboard"]);
    }

    private async Task UnbanInlineAsync(int banId)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["ConfirmUnban"], banId),
            L["Unban"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Unban"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;
        await _operations.RunOperationAsync(
            () => Api.Teamspeak.UnbanTeamspeakClientAsync(ServerId, banId),
            L["Unban"]);
    }

    private Task LoadClientsDataAsync(GridLoadArgs args) => _data.LoadClientsAsync(args);

    private Task LoadChannelsDataAsync(GridLoadArgs args) => _data.LoadChannelsAsync(args);

    private Task LoadBansDataAsync(GridLoadArgs args) => _data.LoadBansAsync(args);

    private static string BuildConnectionLink(string hostname, int voicePort) =>
        $"ts3server://{hostname}?port={voicePort}";

    private static string FormatUptime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Days > 0
            ? $"{ts.Days}d {ts.Hours}h {ts.Minutes}m"
            : $"{ts.Hours}h {ts.Minutes}m";
    }

    /// <summary>Recette R2-124: the export writes the channel as the cell does.</summary>
    private static readonly Func<TeamspeakClientDto, object?> ChannelExport = client =>
        string.IsNullOrEmpty(client.ChannelName) ? client.ChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture) : client.ChannelName;
}
