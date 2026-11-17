// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Alerts;

public partial class Alerts : IAsyncDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private UiActions Ui { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private AlertNotificationService AlertNotify { get; set; } = default!;

    // The rules API answers the whole list: the page holds it, and the grid's window (sort, column filters
    // and page cut here, as the grid asks) is what is drawn. The list is read again only when it is stale
    // (first load, after a change, after a failed read), never for a sort, a filter or a scroll.
    private List<AlertRuleDto> _alerts = [];
    private List<AlertRuleDto> _rules = [];
    private int _ruleCount;
    private bool _rulesStale = true;
    private IReadOnlyList<AlertTriggeredDto> _recent = [];
    private AetheusDataGrid<AlertTriggeredDto>? _recentGrid;
    private AetheusDataGrid<AlertRuleDto>? _rulesGrid;

    // Header filter texts, built once so the columns see the same delegate on every render.
    private Func<string, string>? _severityText;
    private Func<string, string>? _metricText;
    private Func<string, string>? _enabledText;
    private Func<string, string> SeverityText => _severityText ??= GridFilterText.ForEnum<AlertSeverity>(L);
    private Func<string, string> MetricText => _metricText ??= GridFilterText.ForEnum<MetricType>(L);
    private Func<string, string> EnabledText => _enabledText ??= value =>
        bool.TryParse(value, out var enabled) ? L[enabled ? "Active" : "Disabled"].Value : value;
    private HubConnection? _hubConnection;
    // Bound to the page: leaving it cancels a hub start that does not answer instead of holding the
    // disposal until the HTTP timeout.
    private readonly CancellationTokenSource _lifetime = new();

    protected override async Task OnInitializedAsync()
    {
        Breadcrumb.Set(new BreadcrumbItem(L["Alerts"]));
        // Re-render when a new fired alert lands so the "Recent alerts" section stays live.
        _recent = AlertNotify.Recent;
        AlertNotify.OnChange += OnRecentAlertsChanged;
        // The rules are the grid's own first request (LoadData); the hub below only refreshes them.
        await ConnectToHub();
    }

    private async Task LoadRulesAsync(GridLoadArgs args)
    {
        if (_rulesStale)
        {
            _alerts = await Api.Monitoring.GetAlertRulesAsync();
            _rulesStale = false;
        }
        _rules = args.ToClientPage(_alerts);
        _ruleCount = args.ClientFilteredCount(_alerts);
    }

    /// <summary>One page of the export, cut from a fresh read of the list with the grid's sort and column filters.</summary>
    private async Task<PaginatedResult<AlertRuleDto>> ReadRulesExportAsync(GridLoadArgs args)
    {
        var all = await Api.Monitoring.GetAlertRulesAsync();
        return new PaginatedResult<AlertRuleDto> { Items = args.ToClientPage(all), TotalCount = args.ClientFilteredCount(all) };
    }

    // The grid takes note of the rows it holds before the new list lands, so the alert that just
    // fired reads bold for a few seconds.
    private void OnRecentAlertsChanged() => InvokeAsync(async () =>
    {
        if (_recentGrid is not null) await _recentGrid.Refresh();
        _recent = AlertNotify.Recent;
        StateHasChanged();
    });

    private void OpenRecentAlert(AlertTriggeredDto alert)
    {
        if (alert.OccurrenceId is { } id) Nav.NavigateTo($"/alerts/{id}");
    }

    // The triggered-alert DTO carries the severity as a string: localize it through the AlertSeverity enum, never show it raw.
    private string RecentSeverityText(string severity) =>
        Enum.TryParse<AlertSeverity>(severity, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? L.Localize(parsed) : severity;

    private static OmniTone RecentSeverityBadge(string severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => OmniTone.Danger,
        "warning" => OmniTone.Warning,
        "info" => OmniTone.Accent,
        _ => OmniTone.Neutral
    };

    // Live refresh on two backend events: AlertTriggered (a rule fired) and AlertRuleChanged
    // (another admin created/edited/deleted a rule), so the list stays current without a manual
    // reload. Best-effort - a hub failure degrades silently to static.
    private async Task ConnectToHub()
    {
        try
        {
            _hubConnection = HubFactory.Create("alerts");
            _hubConnection.On<object>("AlertTriggered", _ => InvokeAsync(ReloadFromHubAsync));
            _hubConnection.On<int>("AlertRuleChanged", _ => InvokeAsync(ReloadFromHubAsync));
            // Group membership is per-connection and lost on auto-reconnect - re-join + reload.
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinAlertGroup");
                await ReloadFromHubAsync();
            }));
            await _hubConnection.StartAsync(_lifetime.Token);
            await _hubConnection.InvokeAsync("JoinAlertGroup", _lifetime.Token);
        }
        catch { /* Hub unavailable - degrade to static */ }
    }

    private async Task ReloadFromHubAsync()
    {
        try
        {
            _alerts = await Api.Monitoring.GetAlertRulesAsync();
            _rulesStale = false;
            // The grid cuts its window from the new list and notes the rule created elsewhere, which reads bold.
            if (_rulesGrid is not null) await _rulesGrid.Refresh();
        }
        catch (HttpRequestException) { /* hub-triggered reload - silent on auth failure */ }
        StateHasChanged();
    }

    // X4D8: create/edit run through AlertEditDialog; the page stays a pure list. The dialog closes
    // with `true` on success, which is our cue to reload (the hub event would also reload, but a
    // local reload avoids depending on the round-trip).
    private Task OpenCreateDialogAsync() => OpenEditDialogAsync(null);

    private async Task OpenEditDialogAsync(AlertRuleDto? alert)
    {
        var result = await Dialog.OpenAsync<AlertEditDialog>(
            alert is null ? L["Create"] : L["Edit"],
            new Dictionary<string, object?> { ["Alert"] = alert },
            new OmniDialogOptions { Width = "720px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });

        // R-10: the rule shows as the server answered it, the read behind it confirms.
        if (result is AlertRuleDto saved)
        {
            _rulesStale = true;
            if (_rulesGrid is not null) await _rulesGrid.ShowRowAsync(saved);
        }
        else if (result is true)
        {
            _rulesStale = true;
            if (_rulesGrid is not null) await _rulesGrid.RestoreRowsAsync();
        }
    }

    private async Task DeleteAlert(AlertRuleDto alert)
    {
        var confirmed = await Dialog.Confirm(L["DeleteConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Veil.RunAsync(() => Ui.RunAsync(
            () => Api.Monitoring.DeleteAlertRuleAsync(alert.Id),
            "Deleted",
            async () =>
            {
                // R-10: the rule leaves the grid at once, the quiet read behind it confirms.
                _rulesStale = true;
                if (_rulesGrid is not null) await _rulesGrid.HideRowAsync(alert);
            },
            errorKey: "DeleteFailed",
            successTitleKey: "Deleted"));
    }

    private static OmniTone GetMetricBadgeStyle(MetricType metric) => metric switch
    {
        MetricType.Cpu => OmniTone.Warning,
        MetricType.Memory => OmniTone.Accent,
        MetricType.Disk => OmniTone.Danger,
        MetricType.DiskFree => OmniTone.Danger,
        MetricType.BuildCache => OmniTone.Warning,
        MetricType.Inodes => OmniTone.Danger,
        MetricType.ClockOffset => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    private static OmniTone GetSeverityBadgeStyle(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => OmniTone.Danger,
        AlertSeverity.Warning => OmniTone.Warning,
        AlertSeverity.Info => OmniTone.Accent,
        _ => OmniTone.Neutral
    };

    public async ValueTask DisposeAsync()
    {
        // Disposed once: a second call would cancel the already disposed lifetime.
        if (_lifetime.IsCancellationRequested) return;
        await _lifetime.CancelAsync();
        AlertNotify.OnChange -= OnRecentAlertsChanged;
        if (_hubConnection is not null)
        {
            try { await _hubConnection.InvokeAsync("LeaveAlertGroup"); } catch { /* best-effort */ }
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }
        _lifetime.Dispose();
    }

    /// <summary>The export writes the threshold as the cell does.</summary>
    private static readonly Func<AlertRuleDto, object?> ThresholdExport = alert => $"{AlertThresholdUnit.Symbol(alert.Operator)} {AlertThresholdUnit.Format(alert.Metric, alert.Threshold)}";
}
