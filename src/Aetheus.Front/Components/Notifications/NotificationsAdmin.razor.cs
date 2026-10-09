// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Notifications;

public partial class NotificationsAdmin : ComponentBase
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private UiActions Ui { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;

    private List<NotificationChannelDto> _channels = [];
    private List<NotificationRuleDto> _rules = [];
    private List<NotificationChannelDto> _dialogChannels = [];
    private Aetheus.Front.Components.Shared.AetheusDataGrid<NotificationChannelDto>? _channelsGrid;
    private Aetheus.Front.Components.Shared.AetheusDataGrid<NotificationRuleDto>? _rulesGrid;
    private int _channelTotalCount;
    private int _ruleTotalCount;
    private string _channelSearch = string.Empty;
    private string _ruleSearch = string.Empty;
    private bool _channelsLoading;
    private bool _rulesLoading;
    private readonly HashSet<int> _testing = [];

    // Recette R-224: the grids' header filters, sent with every page request, and the values they offer.
    private List<GridFilter> _channelFilters = [];
    private List<GridFilter> _ruleFilters = [];
    private NotificationAdminFilterValuesDto _ruleFilterValues = new();
    private Func<string, string>? _channelTypeText;
    private Func<string, string>? _enabledText;
    private Func<string, string> ChannelTypeText => _channelTypeText ??= GridFilterText.ForEnum<NotificationChannelType>(L);
    private Func<string, string> EnabledText => _enabledText ??= value =>
        bool.TryParse(value, out var enabled) ? L[enabled ? "Active" : "Disabled"].Value : value;
    private readonly Dictionary<int, NotificationTestResultDto> _channelTest = [];

    protected override async Task OnInitializedAsync()
    {
        if (!Auth.IsAdmin)
        {
            Nav.NavigateTo("/");
            return;
        }
        Breadcrumb.Set(new BreadcrumbItem(L["Administration"], "/admin"), new BreadcrumbItem(L["NotificationRules"]));
        await LoadRuleFilterValuesAsync();
    }

    private async Task LoadRuleFilterValuesAsync()
    {
        try { _ruleFilterValues = await Api.Monitoring.GetNotificationRuleFilterValuesAsync(); }
        catch (HttpRequestException) { _ruleFilterValues = new NotificationAdminFilterValuesDto(); }
    }

    /// <summary>Recette R2-124: one page of the channels export, with the search, sort and column filters of the grid.</summary>
    internal Task<PaginatedResult<NotificationChannelDto>> ReadChannelsExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = GetSort(args, "Name");
        return Api.Monitoring.GetNotificationChannelsPagedAsync(
            page, pageSize, _channelSearch, sortBy, sortDescending, args.ToApiFilters());
    }

    /// <summary>Recette R2-124: one page of the rules export, with the search, sort and column filters of the grid.</summary>
    internal Task<PaginatedResult<NotificationRuleDto>> ReadRulesExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = GetSort(args, "EventType");
        return Api.Monitoring.GetNotificationRulesPagedAsync(
            page, pageSize, _ruleSearch, sortBy, sortDescending, args.ToApiFilters());
    }

    internal async Task LoadChannelsAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = GetSort(args, "Name");
        _channelFilters = args.ToApiFilters();
        _channelsLoading = true;
        try
        {
            var result = await Api.Monitoring.GetNotificationChannelsPagedAsync(
                page, pageSize, _channelSearch, sortBy, sortDescending, _channelFilters);
            _channels = result.Items;
            _channelTotalCount = result.TotalCount;
        }
        catch (HttpRequestException) { /* 401 redirect handled upstream */ }
        finally { _channelsLoading = false; }
    }

    internal async Task LoadRulesAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = GetSort(args, "EventType");
        _ruleFilters = args.ToApiFilters();
        _rulesLoading = true;
        try
        {
            var result = await Api.Monitoring.GetNotificationRulesPagedAsync(
                page, pageSize, _ruleSearch, sortBy, sortDescending, _ruleFilters);
            _rules = result.Items;
            _ruleTotalCount = result.TotalCount;
        }
        catch (HttpRequestException) { /* 401 redirect handled upstream */ }
        finally { _rulesLoading = false; }
    }

    private async Task ReloadAllAsync()
    {
        // A created, renamed or deleted channel or rule changes the values the rule filters offer.
        await LoadRuleFilterValuesAsync();
        var channelReload = _channelsGrid?.Reload() ?? Task.CompletedTask;
        var ruleReload = _rulesGrid?.Reload() ?? Task.CompletedTask;
        await Task.WhenAll(channelReload, ruleReload);
    }

    private Task ResetChannelSearchAsync() => _channelsGrid?.GoToPage(0, forceReload: true) ?? Task.CompletedTask;
    private Task ResetRuleSearchAsync() => _rulesGrid?.GoToPage(0, forceReload: true) ?? Task.CompletedTask;

    private static (string SortBy, bool Descending) GetSort(GridLoadArgs args, string fallback)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return (fallback, false);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1 &&
            parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

    private async Task OpenChannelDialogAsync(NotificationChannelDto? channel)
    {
        var result = await Dialog.OpenAsync<NotificationChannelEditDialog>(
            channel is null ? L["Create"] : L["Edit"],
            new Dictionary<string, object?> { ["ChannelId"] = channel?.Id },
            new OmniDialogOptions { Width = "640px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
        if (result is true)
        {
            _dialogChannels = [];
            await ReloadAllAsync();
        }
    }

    private async Task OpenRuleDialogAsync(NotificationRuleDto? rule)
    {
        if (_dialogChannels.Count == 0)
            _dialogChannels = await Api.Monitoring.GetNotificationChannelsAsync();
        var result = await Dialog.OpenAsync<NotificationRuleEditDialog>(
            rule is null ? L["Create"] : L["Edit"],
            new Dictionary<string, object?> { ["Rule"] = rule, ["Channels"] = _dialogChannels },
            new OmniDialogOptions { Width = "640px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
        if (result is true && _rulesGrid is not null) await _rulesGrid.Reload();
    }

    private async Task TestChannelAsync(int id)
    {
        _testing.Add(id);
        _channelTest.Remove(id);
        StateHasChanged();
        try
        {
            var result = await Api.Monitoring.TestNotificationChannelAsync(id);
            _channelTest[id] = result ?? new NotificationTestResultDto
            {
                Status = NotificationTestStatus.Failed,
                Message = L["NotificationChannelNotFound"]
            };
            var testResult = _channelTest[id];
            Notify.Notify(
                testResult.Status == NotificationTestStatus.Sent
                    ? OmniSeverity.Success
                    : OmniSeverity.Danger,
                testResult.Status == NotificationTestStatus.Sent ? "Saved" : "Error",
                testResult.Message ?? L["NotificationChannelTestFailed"]);
        }
        catch (HttpRequestException)
        {
            _channelTest[id] = new NotificationTestResultDto { Status = NotificationTestStatus.Failed, Message = L["NotificationChannelTestFailed"] };
            Notify.Error("Error", "NotificationChannelTestFailed");
        }
        finally
        {
            _testing.Remove(id);
            StateHasChanged();
        }
    }

    private async Task DeleteChannelAsync(NotificationChannelDto channel)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["DeleteNotificationChannelConfirm"], channel.Name), L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Ui.RunAsync(
            () => Api.Monitoring.DeleteNotificationChannelAsync(channel.Id),
            "Deleted",
            async () =>
            {
                _channelTest.Remove(channel.Id);
                _dialogChannels = [];
                await ReloadAllAsync();
            },
            errorKey: "DeleteFailed",
            successTitleKey: "Deleted");
    }

    private async Task DeleteRuleAsync(NotificationRuleDto rule)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["DeleteNotificationRuleConfirm"], rule.EventType), L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Ui.RunAsync(
            () => Api.Monitoring.DeleteNotificationRuleAsync(rule.Id),
            "Deleted",
            () => _rulesGrid?.Reload() ?? Task.CompletedTask,
            errorKey: "DeleteFailed",
            successTitleKey: "Deleted");
    }

    private static OmniTone TestBadge(NotificationTestStatus status) => status switch
    {
        NotificationTestStatus.Sent => OmniTone.Success,
        NotificationTestStatus.NotConfigured => OmniTone.Neutral,
        _ => OmniTone.Warning
    };
}
