// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerMailSection
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ILogger<ServerMailSection> Logger { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;
    [Parameter, EditorRequired] public int ServerId { get; set; }

    // Recette R2-089: the "installed but not configured" warning waits for the counts to be read.
    private bool _countsRead;
    private bool _countsReading;
    private ServerMailReinstall? _reinstall;

    // Data
    private List<MailDomainDto> _domains = [];
    private List<MailAccountDto> _accounts = [];
    private List<MailDomainDto> _dialogDomains = [];
    // Recette R-327: the grids scroll and fetch further blocks; what the page derives from them (the
    // Relations tab resources, the default sender, the dialog domains) reads their first block, so
    // scrolling a grid does not change it.
    private List<MailDomainDto> _firstDomains = [];
    private List<MailAccountDto> _firstAccounts = [];
    private int _domainCount;
    private int _accountCount;
    private int _aliasCount;
    private bool _domainsLoading;
    private bool _accountsLoading;
    private bool _aliasesLoading;
    private int? _loadedServerId;
    private int _loadGeneration;
    private Aetheus.Front.Components.Shared.AetheusDataGrid<MailDomainDto>? _domainsGrid;
    private Aetheus.Front.Components.Shared.AetheusDataGrid<MailAccountDto>? _accountsGrid;
    private Aetheus.Front.Components.Shared.AetheusDataGrid<MailAliasDto>? _aliasesGrid;

    // Add domain dialog
    private string _newDomainName = string.Empty;
    private string _newDkimSelector = "default";

    // Add account dialog
    private string _newAccountEmail = string.Empty;
    private string _newAccountPassword = string.Empty;
    private int _newAccountQuota = 1024;

    // DNS records dialog
    private MailDnsRecordsDto? _dnsRecords;

    // Aliases
    private List<MailAliasDto> _aliases = [];
    private string _newAliasSource = string.Empty;
    private string _newAliasDest = string.Empty;
    private int _selectedAliasDomainId;

    // DKIM rotation dialog
    private string _dkimRotationDomain = string.Empty;
    private string _dkimCurrentSelector = string.Empty;
    private string _dkimNewSelector = string.Empty;
    private int _dkimRotationDomainId;

    private MailDataDto Mail => Server.Mail;

    // PLAN-005 child tabs that follow the output of the tasks they queue.
    private ServerMailQueueTab? _queueTab;
    private ServerMailLogsTab? _logsTab;
    private ServerMailStatusList? _statusList;
    private ServerMailSetupAssistant? _assistant;

    // First known account of the server: a sensible default sender for the delivery test.
    private string DefaultSender => _firstAccounts.FirstOrDefault(a => a.IsActive)?.Email ?? string.Empty;

    private List<string> MailResourceNames => _firstDomains.Select(d => d.Name).ToList();

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId != ServerId)
        {
            _loadedServerId = ServerId;
            Interlocked.Increment(ref _loadGeneration);
            _domains = [];
            _accounts = [];
            _aliases = [];
            _dialogDomains = [];
            _firstDomains = [];
            _firstAccounts = [];
            _domainCount = 0;
            _accountCount = 0;
            _aliasCount = 0;
            _countsRead = false;
            if (Mail.IsInstalled)
                await ReloadDataAsync();
        }
        // A stack erased then configured again is counted again.
        if (!Mail.IsInstalled)
            _countsRead = false;
        else if (!_countsRead && !_countsReading)
            await ReadCountsAsync();
    }

    // The grids load only the tabs that are shown; the warning needs both counts whatever the tab.
    private async Task ReadCountsAsync()
    {
        var serverId = ServerId;
        var generation = _loadGeneration;
        _countsReading = true;
        try
        {
            var domains = Api.Mail.GetMailDomainsPageAsync(ServerId, 1, 1);
            var accounts = Api.Mail.GetMailAccountsPageAsync(ServerId, 1, 1);
            await Task.WhenAll(domains, accounts);
            if (serverId != ServerId || generation != _loadGeneration) return;
            _domainCount = (await domains).TotalCount;
            _accountCount = (await accounts).TotalCount;
            _countsRead = true;
        }
        catch (HttpRequestException ex)
        {
            // Nothing concluded: the loader stays and the next heartbeat reads the counts again.
            Logger.LogWarning(ex, "Mail counts read failed for server {ServerId}", serverId);
        }
        finally
        {
            _countsReading = false;
        }
    }

    private async Task ReloadDataAsync()
    {
        var domainsReload = _domainsGrid?.Reload() ?? Task.CompletedTask;
        var accountsReload = _accountsGrid?.Reload() ?? Task.CompletedTask;
        var aliasesReload = _aliasesGrid?.Reload() ?? Task.CompletedTask;
        await Task.WhenAll(domainsReload, accountsReload, aliasesReload);
    }

    // Recette R-226: a completed task is live data, so the grids refresh quietly (rows, page, filters kept).
    private async Task RefreshDataAsync()
    {
        var domainsRefresh = _domainsGrid?.Refresh() ?? Task.CompletedTask;
        var accountsRefresh = _accountsGrid?.Refresh() ?? Task.CompletedTask;
        var aliasesRefresh = _aliasesGrid?.Refresh() ?? Task.CompletedTask;
        await Task.WhenAll(domainsRefresh, accountsRefresh, aliasesRefresh);
    }

    // Recette R2-124: one page of each list's export, with the grid's sort and column filters.
    private Task<PaginatedResult<MailDomainDto>> ReadDomainsExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = GetSort(args, "Name");
        return Api.Mail.GetMailDomainsPageAsync(ServerId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: args.ToApiFilters());
    }

    private Task<PaginatedResult<MailAccountDto>> ReadAccountsExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = GetSort(args, "Email");
        return Api.Mail.GetMailAccountsPageAsync(ServerId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: args.ToApiFilters());
    }

    private Task<PaginatedResult<MailAliasDto>> ReadAliasesExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = GetSort(args, "SourceEmail");
        return Api.Mail.GetMailAliasesPageAsync(ServerId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: args.ToApiFilters());
    }

    private async Task LoadDomainsAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = GetSort(args, "Name");
        // Recette R-210: the header filters, applied by the API.
        var filters = args.ToApiFilters();
        await LoadPageAsync(
            () => Api.Mail.GetMailDomainsPageAsync(ServerId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: filters),
            result =>
            {
                _domains = result.Items;
                _domainCount = result.TotalCount;
                if (page == 1) _firstDomains = result.Items;
            },
            value => _domainsLoading = value);
    }

    private async Task LoadAccountsAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = GetSort(args, "Email");
        // Recette R-210 / R-224: the header filters, applied by the API.
        var filters = args.ToApiFilters();
        await LoadPageAsync(
            () => Api.Mail.GetMailAccountsPageAsync(ServerId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: filters),
            result =>
            {
                _accounts = result.Items;
                _accountCount = result.TotalCount;
                if (page == 1) _firstAccounts = result.Items;
            },
            value => _accountsLoading = value);
    }

    private async Task LoadAliasesAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = GetSort(args, "SourceEmail");
        // Recette R-210: the header filters, applied by the API.
        var filters = args.ToApiFilters();
        await LoadPageAsync(
            () => Api.Mail.GetMailAliasesPageAsync(ServerId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: filters),
            result => { _aliases = result.Items; _aliasCount = result.TotalCount; },
            value => _aliasesLoading = value);
    }

    private async Task LoadPageAsync<T>(
        Func<Task<PaginatedResult<T>>> load,
        Action<PaginatedResult<T>> apply,
        Action<bool> setLoading)
    {
        var serverId = ServerId;
        var generation = _loadGeneration;
        setLoading(true);
        try
        {
            var result = await load();
            if (serverId == ServerId && generation == _loadGeneration)
                apply(result);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail page load failed for server {ServerId}", serverId);
            if (serverId == ServerId && generation == _loadGeneration)
                Toast.Error(L["LoadFailed"]);
        }
        finally
        {
            if (serverId == ServerId && generation == _loadGeneration)
                setLoading(false);
        }
    }

    private static (string SortBy, bool Descending) GetSort(GridLoadArgs args, string fallback)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return (fallback, false);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

    private async Task VerifyDnsAsync(MailDomainDto domain)
    {
        var check = await Api.Mail.VerifyMailDnsAsync(ServerId, domain.Id);
        if (check is null)
        {
            Toast.Error(L["ActionFailed"]);
            return;
        }
        Toast.Success("MailVerifyDns");
        await Dialog.OpenAsync<MailDnsCheckDialog>($"{L["MailVerifyDns"]}: {domain.Name}",
            new Dictionary<string, object?> { { "Check", check } },
            new OmniDialogOptions { Width = "44rem", AutoFocusFirstElement = false });
        await ReloadDataAsync();
    }

    // Quota 0 = adopted mailbox whose quota Aetheus does not manage: show the measured size only.
    internal string QuotaText(MailAccountDto account) => account.QuotaMb > 0
        ? $"{account.UsedMb} / {account.QuotaMb} MB ({UsagePercent(account)}%)"
        : $"{account.UsedMb} MB ({L["MailQuotaUnmanaged"]})";

    internal static double UsagePercent(MailAccountDto account) =>
        account.QuotaMb > 0 ? Math.Round(account.UsedMb * 100.0 / account.QuotaMb, 1) : 0;

    internal static OmniTone QuotaStyle(MailAccountDto account) => UsagePercent(account) switch
    {
        > 90 => OmniTone.Danger,
        >= 70 => OmniTone.Warning,
        _ => OmniTone.Success
    };

    private async Task CreateDomainAsync()
    {
        try
        {
            var result = await Api.Mail.CreateMailDomainAsync(ServerId, new CreateMailDomainRequest
            {
                Name = _newDomainName,
                DkimSelector = _newDkimSelector
            });
            if (result is not null)
            {
                Toast.Success(L["TaskQueued"]);
                _newDomainName = string.Empty;
                _newDkimSelector = "default";
                await ReloadDataAsync();
            }
            else
            {
                Toast.Error(L["ActionFailed"]);
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail domain creation failed for server {ServerId}", ServerId);
            Toast.Error(L["ActionFailed"]);
        }
    }

    private async Task OpenAddDomainDialogAsync()
    {
        var result = await OpenMailDialogAsync(MailDialogMode.AddDomain, L["AddDomain"], new MailDialogModel { Domain = _newDomainName, DkimSelector = _newDkimSelector });
        if (result is null) return;
        _newDomainName = result.Domain;
        _newDkimSelector = result.DkimSelector;
        await CreateDomainAsync();
    }

    private async Task DeleteDomainAsync(int domainId)
    {
        var success = await Api.Mail.DeleteMailDomainAsync(ServerId, domainId);
        if (success)
        {
            Toast.Success(L["TaskQueued"]);
            await ReloadDataAsync();
        }
        else
        {
            Toast.Error(L["ActionFailed"]);
        }
    }

    private async Task CreateAccountAsync()
    {
        try
        {
            var result = await Api.Mail.CreateMailAccountAsync(ServerId, new CreateMailAccountRequest
            {
                Email = _newAccountEmail,
                Password = _newAccountPassword,
                QuotaMb = _newAccountQuota
            });
            if (result is not null)
            {
                Toast.Success(L["TaskQueued"]);
                _newAccountEmail = string.Empty;
                _newAccountPassword = string.Empty;
                _newAccountQuota = 1024;
                await ReloadDataAsync();
            }
            else
            {
                Toast.Error(L["ActionFailed"]);
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail account creation failed for server {ServerId}", ServerId);
            Toast.Error(L["ActionFailed"]);
        }
    }

    private async Task OpenAddAccountDialogAsync()
    {
        var result = await OpenMailDialogAsync(MailDialogMode.AddAccount, L["AddAccount"], new MailDialogModel { Email = _newAccountEmail, Password = _newAccountPassword, QuotaMb = _newAccountQuota });
        if (result is null) return;
        _newAccountEmail = result.Email;
        _newAccountPassword = result.Password;
        _newAccountQuota = result.QuotaMb;
        await CreateAccountAsync();
    }

    private async Task DeleteAccountAsync(int accountId)
    {
        var success = await Api.Mail.DeleteMailAccountAsync(ServerId, accountId);
        if (success)
        {
            Toast.Success(L["TaskQueued"]);
            await ReloadDataAsync();
        }
        else
        {
            Toast.Error(L["ActionFailed"]);
        }
    }

    private async Task ShowDnsRecordsAsync(MailDomainDto domain)
    {
        try
        {
            _dnsRecords = await Api.Mail.GetMailDnsRecordsAsync(ServerId, domain.Id);
            await Dialog.OpenAsync<MailOperationDialog>(
                $"{L["DnsRecords"]}: {_dnsRecords?.Domain}",
                new Dictionary<string, object?> { { "Mode", MailDialogMode.DnsRecords }, { "DnsRecords", _dnsRecords } },
                new OmniDialogOptions { Width = "42rem", AutoFocusFirstElement = false });
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail DNS lookup failed for server {ServerId}, domain {DomainId}", ServerId, domain.Id);
            Toast.Error(L["ActionFailed"]);
        }
    }

    private async Task<MailDialogModel?> OpenMailDialogAsync(MailDialogMode mode, string title, MailDialogModel model, string width = "32rem")
    {
        if (mode is MailDialogMode.AddAccount or MailDialogMode.AddAlias && _dialogDomains.Count == 0)
            _dialogDomains = await Api.Mail.GetMailDomainsAsync(ServerId);
        var result = await Dialog.OpenAsync<MailOperationDialog>(title,
            new Dictionary<string, object?>
            {
                { "Mode", mode },
                { "Model", model },
                { "ServerId", ServerId },
                { "Domains", _dialogDomains.Count > 0 ? _dialogDomains : _firstDomains }
            },
            new OmniDialogOptions { Width = width, AutoFocusFirstElement = false });
        return result as MailDialogModel;
    }

    /// <summary>Refreshes the grids quietly and lets every child tab pick up the output of the task it queued.</summary>
    public void HandleTaskCompleted(TaskCompletedNotification notification)
    {
        _ = InvokeAsync(async () =>
        {
            var setupDone = _assistant?.OwnsSetupTask(notification.TaskId) == true;
            if (Mail.IsInstalled)
                await Task.WhenAll(RefreshDataAsync(), setupDone ? ReadCountsAsync() : Task.CompletedTask);
            if (_reinstall is not null) await _reinstall.HandleTaskCompletedAsync(notification);
            if (_queueTab is not null) await _queueTab.HandleTaskCompletedAsync(notification);
            if (_logsTab is not null) await _logsTab.HandleTaskCompletedAsync(notification);
            if (_statusList is not null) await _statusList.HandleTaskCompletedAsync(notification);
            if (_assistant is not null) await _assistant.HandleTaskCompletedAsync(notification);
            StateHasChanged();
        });
    }

    private async Task ShowConfirm(string title, string message, Func<Task> action)
    {
        var confirmed = await Dialog.Confirm(message, title,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed == true)
            await action();
    }

    private async Task CopyToClipboardAsync(string text)
    {
        await Js.InvokeVoidAsync("navigator.clipboard.writeText", text);
        Toast.Success(L["CopiedToClipboard"]);
    }

    // --- Aliases ---

    private async Task CreateAliasAsync()
    {
        var result = await Api.Mail.CreateMailAliasAsync(ServerId, _selectedAliasDomainId, new CreateMailAliasRequest
        {
            SourceEmail = _newAliasSource,
            DestinationEmail = _newAliasDest
        });
        if (result is not null)
        {
            _newAliasSource = string.Empty;
            _newAliasDest = string.Empty;
            Toast.Success(L["AliasCreated"]);
            if (_aliasesGrid is not null) await _aliasesGrid.Reload();
        }
        else
            Toast.Error(L["OperationFailed"]);
    }

    private async Task OpenAddAliasDialogAsync()
    {
        var result = await OpenMailDialogAsync(MailDialogMode.AddAlias, L["AddAlias"], new MailDialogModel
        {
            Source = _newAliasSource,
            Destination = _newAliasDest,
            DomainId = _selectedAliasDomainId > 0
                ? _selectedAliasDomainId
                : _dialogDomains.FirstOrDefault()?.Id ?? _firstDomains.FirstOrDefault()?.Id ?? 0
        });
        if (result is null) return;
        _newAliasSource = result.Source;
        _newAliasDest = result.Destination;
        _selectedAliasDomainId = result.DomainId;
        await CreateAliasAsync();
    }

    private async Task DeleteAliasAsync(int aliasId)
    {
        if (await Api.Mail.DeleteMailAliasAsync(ServerId, aliasId))
        {
            Toast.Success(L["AliasDeleted"]);
            if (_aliasesGrid is not null) await _aliasesGrid.Reload();
        }
        else
            Toast.Error(L["OperationFailed"]);
    }

    // --- DKIM key rotation ---

    private async Task ShowDkimRotation(MailDomainDto domain)
    {
        _dkimRotationDomainId = domain.Id;
        _dkimRotationDomain = domain.Name;
        _dkimCurrentSelector = domain.DkimSelector;
        _dkimNewSelector = string.Empty;
        var result = await OpenMailDialogAsync(MailDialogMode.RotateDkim, L["RotateDkimKey"], new MailDialogModel
        {
            Domain = _dkimRotationDomain,
            CurrentSelector = _dkimCurrentSelector,
            NewSelector = _dkimNewSelector
        });
        if (result is null) return;
        _dkimNewSelector = result.NewSelector;
        await RotateDkimKeyAsync();
    }

    private async Task RotateDkimKeyAsync()
    {
        var result = await Api.Mail.RotateMailDkimKeyAsync(ServerId, _dkimRotationDomainId, new DkimRotationRequest
        {
            NewSelector = _dkimNewSelector
        });
        if (result is not null)
        {
            Toast.Success(L["DkimKeyRotated"]);
            await ReloadDataAsync();
        }
        else
            Toast.Error(L["OperationFailed"]);
    }


    /// <summary>Recette R2-124: the export writes the DNS records the domain has, and the quota as the cell does.</summary>
    private static readonly Func<MailDomainDto, object?> DnsExport = domain => string.Join(", ",
        new[] { (domain.HasSpf, "SPF"), (domain.HasDkim, "DKIM"), (domain.HasDmarc, "DMARC") }.Where(record => record.Item1).Select(record => record.Item2));
    private Func<MailAccountDto, object?> QuotaExport => _quotaExport ??= QuotaText;
    private Func<MailAccountDto, object?>? _quotaExport;
}
