// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerPortsentrySection
{
    [Parameter] public int ServerId { get; set; }
    [Parameter] public PortsentryDataDto Ps { get; set; } = new();
    [Parameter] public EventCallback OnRefreshRequested { get; set; }

    // SITE-OFFLINE-001: the host page passes the PortSentry data only; the server (and its agent's
    // presence) comes from the loader the server detail layout cascades to every section. The whitelist
    // is stored by the backend alone and stays editable; every other action queues an agent task.
    [CascadingParameter(Name = "ServerLoader")] public ServerDetailLoader? Loader { get; set; }

    private ServerDetailDto? AgentServer => Loader?.Server;
    private bool AgentOffline => AgentServer is { } server && AgentOfflineGuard.IsOffline(server);
    private bool BlockedByOfflineAgent() => AgentServer is { } server && AgentOfflineGuard.Blocks(server, Toast);

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    private bool _actionRunning;
    private string _setupMode = "atcp";
    private string _setupTcpPorts = "1,11,15,79,111,119,143,540,635,1080,1524,2000,5742,6667,12345,12346,20034,27665,31337,32771,32772,32773,32774,40421,49724,54320";
    private string _setupUdpPorts = "1,7,9,69,161,162,513,635,640,641,700,32770,32771,32772,32773,32774,31337,54321";
    private List<PortsentryWhitelistIpDto> _whitelist = [];
    private List<PortsentryBlockedIpDto> _blockedIps = [];
    private string _whitelistIp = string.Empty;
    private string _whitelistDesc = string.Empty;
    private int _blockedCount;
    private int _whitelistCount;
    private bool _blockedLoading;
    private bool _whitelistLoading;
    private int? _loadedServerId;
    private int _loadGeneration;
    private Aetheus.Front.Components.Shared.AetheusDataGrid<PortsentryBlockedIpDto>? _blockedGrid;
    private Aetheus.Front.Components.Shared.AetheusDataGrid<PortsentryWhitelistIpDto>? _whitelistGrid;
    private List<string> _protocolValues = [];
    private ServerTaskFollower? _follower;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId == ServerId) return;
        _loadedServerId = ServerId;
        Interlocked.Increment(ref _loadGeneration);
        _blockedIps = [];
        _whitelist = [];
        _blockedCount = 0;
        _whitelistCount = 0;
        _whitelistIp = string.Empty;
        _protocolValues = [];
        _whitelistDesc = string.Empty;

        if (!Ps.IsInstalled) return;
        var blockedReload = _blockedGrid?.Reload() ?? Task.CompletedTask;
        var whitelistReload = _whitelistGrid?.Reload() ?? Task.CompletedTask;
        await Task.WhenAll(blockedReload, whitelistReload, LoadProtocolValuesAsync());
    }

    // The protocols the Protocol header filter offers, across every blocked IP of the server.
    private async Task LoadProtocolValuesAsync()
    {
        var serverId = ServerId;
        var generation = _loadGeneration;
        try
        {
            var values = await Api.Security.GetPortsentryFilterValuesAsync(serverId);
            if (serverId == ServerId && generation == _loadGeneration) _protocolValues = values.Protocols;
        }
        catch (HttpRequestException)
        {
            if (serverId == ServerId && generation == _loadGeneration) _protocolValues = [];
        }
    }

    // One page of each list's export, with the grid's sort and column filters.
    private Task<PaginatedResult<PortsentryBlockedIpDto>> ReadBlockedExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = GetSort(args, "BlockedAt", fallbackDescending: true);
        return Api.Security.GetPortsentryBlockedIpsAsync(ServerId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: args.ToApiFilters());
    }

    private Task<PaginatedResult<PortsentryWhitelistIpDto>> ReadWhitelistExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = GetSort(args, "IpAddress", fallbackDescending: false);
        return Api.Security.GetPortsentryWhitelistPageAsync(ServerId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: args.ToApiFilters());
    }

    private async Task LoadBlockedAsync(GridLoadArgs args)
    {
        var serverId = ServerId;
        var generation = _loadGeneration;
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = GetSort(args, "BlockedAt", fallbackDescending: true);
        // The header filters, applied by the API.
        var filters = args.ToApiFilters();
        _blockedLoading = true;
        try
        {
            var result = await Api.Security.GetPortsentryBlockedIpsAsync(serverId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: filters);
            if (serverId == ServerId && generation == _loadGeneration)
            {
                _blockedIps = result.Items;
                _blockedCount = result.TotalCount;
            }
        }
        catch (HttpRequestException)
        {
            if (serverId == ServerId && generation == _loadGeneration)
            {
                _blockedIps = [];
                _blockedCount = 0;
                Toast.Error("Error", "LoadFailed");
            }
        }
        finally
        {
            if (serverId == ServerId && generation == _loadGeneration)
                _blockedLoading = false;
        }
    }

    private async Task LoadWhitelistAsync(GridLoadArgs args)
    {
        var serverId = ServerId;
        var generation = _loadGeneration;
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = GetSort(args, "IpAddress", fallbackDescending: false);
        // The header filters, applied by the API.
        var filters = args.ToApiFilters();
        _whitelistLoading = true;
        try
        {
            var result = await Api.Security.GetPortsentryWhitelistPageAsync(serverId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: filters);
            if (serverId == ServerId && generation == _loadGeneration)
            {
                _whitelist = result.Items;
                _whitelistCount = result.TotalCount;
            }
        }
        catch (HttpRequestException)
        {
            if (serverId == ServerId && generation == _loadGeneration)
            {
                _whitelist = [];
                _whitelistCount = 0;
                Toast.Error("Error", "LoadFailed");
            }
        }
        finally
        {
            if (serverId == ServerId && generation == _loadGeneration)
                _whitelistLoading = false;
        }
    }

    private static (string SortBy, bool Descending) GetSort(
        GridLoadArgs args, string fallback, bool fallbackDescending)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return (fallback, fallbackDescending);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

    private async Task ExecuteActionAsync(PortsentryAction action)
    {
        if (BlockedByOfflineAgent()) return;
        _actionRunning = true;
        // Recette R3-37: the follower shows what becomes of the task, in place of a lone "Task created".
        _follower?.Expect(L[action.ToString()]);
        try
        {
            var success = await Api.Security.ExecutePortsentryActionAsync(ServerId, new PortsentryActionRequest { Action = action });
            if (success)
            {
                Toast.Success("TaskCreated");
                await OnRefreshRequested.InvokeAsync();
            }
            else
            {
                _follower?.Forget();
                Toast.Error("Error", "ActionFailed");
            }
        }
        catch (HttpRequestException)
        {
            _follower?.Forget();
            Toast.Error("Error", "ActionFailed");
        }
        finally
        {
            _actionRunning = false;
        }
    }

    private async Task SetupAsync()
    {
        if (BlockedByOfflineAgent()) return;
        var request = new PortsentrySetupRequest
        {
            Mode = _setupMode,
            TcpPorts = _setupTcpPorts,
            UdpPorts = _setupUdpPorts
        };
        if (await ServerTaskLaunch.FollowAsync(_follower, L["PortsentrySetup"], () => Api.Security.SetupPortsentryAsync(ServerId, request)))
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
            L["PortsentrySetup"].Value,
            new Dictionary<string, object?>
            {
                { "Kind", ServerSetupKind.Portsentry },
                { "Model", new ServerSetupDialogModel { Mode = _setupMode, TcpPorts = _setupTcpPorts, UdpPorts = _setupUdpPorts } }
            },
            new OmniDialogOptions { Width = "38rem", AutoFocusFirstElement = false });
        if (result is not ServerSetupDialogModel model) return;
        _setupMode = model.Mode;
        _setupTcpPorts = model.TcpPorts;
        _setupUdpPorts = model.UdpPorts;
        await SetupAsync();
    }

    private async Task GetLogsAsync()
    {
        if (BlockedByOfflineAgent()) return;
        _follower?.Expect(L["ViewLogs"]);
        var success = await Api.Security.GetPortsentryLogsAsync(ServerId, new PortsentryLogRequest());
        if (success)
            Toast.Success("TaskCreated");
        else
        {
            _follower?.Forget();
            Toast.Error("Error", "ActionFailed");
        }
    }

    private async Task UnblockIpAsync(string ip)
    {
        if (BlockedByOfflineAgent()) return;
        var confirmed = await Dialog.Confirm(
            L["UnblockConfirm"].Value,
            L["Unblock"].Value,
            new OmniConfirmOptions { OkButtonText = L["Unblock"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        _follower?.Expect($"{L["Unblock"]} {ip}");
        var success = await Api.Security.UnblockPortsentryIpAsync(ServerId, new PortsentryUnblockRequest { IpAddress = ip });
        if (success)
        {
            Toast.Success("TaskCreated");
            await OnRefreshRequested.InvokeAsync();
        }
        else
        {
            _follower?.Forget();
            Toast.Error("Error", "ActionFailed");
        }
    }

    private async Task AddWhitelistIpAsync()
    {
        if (string.IsNullOrWhiteSpace(_whitelistIp)) return;
        var result = await Api.Security.AddPortsentryWhitelistIpAsync(ServerId, new AddPortsentryWhitelistRequest
        {
            IpAddress = _whitelistIp.Trim(),
            Description = _whitelistDesc.Trim()
        });
        if (result is not null)
        {
            _whitelistIp = string.Empty;
            _whitelistDesc = string.Empty;
            Toast.Success("Saved");
            if (_whitelistGrid is not null) await _whitelistGrid.Reload();
        }
        else
            Toast.Error("Error", "SaveFailed");
    }

    private async Task RemoveWhitelistIpAsync(PortsentryWhitelistIpDto entry)
    {
        var confirmed = await Dialog.Confirm(
            $"{L["DeleteConfirm"].Value} ({entry.IpAddress})",
            L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var success = await Api.Security.RemovePortsentryWhitelistIpAsync(ServerId, entry.Id);
        if (success)
        {
            Toast.Success("Deleted");
            if (_whitelistGrid is not null)
                await _whitelistGrid.Reload();
            else
            {
                _whitelist.RemoveAll(item => item.Id == entry.Id);
                _whitelistCount = Math.Max(0, _whitelistCount - 1);
            }
        }
        else
            Toast.Error("Error", "DeleteFailed");
    }

    private static OmniTone GetBlockedCountBadgeStyle(int count) => count switch
    {
        0 => OmniTone.Success,
        <= 5 => OmniTone.Warning,
        _ => OmniTone.Danger
    };
}
