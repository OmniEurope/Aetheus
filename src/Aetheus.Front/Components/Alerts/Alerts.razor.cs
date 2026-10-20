// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Alerts;

public partial class Alerts : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private UiActions Ui { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private AlertNotificationService AlertNotify { get; set; } = default!;

    private List<AlertRuleDto> _alerts = [];
    private IReadOnlyList<AlertTriggeredDto> _recent = [];
    private AetheusDataGrid<AlertTriggeredDto>? _recentGrid;
    private AetheusDataGrid<AlertRuleDto>? _rulesGrid;
    private bool _loading = true;

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
        try { _alerts = await Api.Monitoring.GetAlertRulesAsync(); }
        catch (HttpRequestException) { _alerts = []; }
        _loading = false;
        // The rules are drawn as soon as they are read: the hub below only refreshes them, and
        // ComponentBase would otherwise hold this render until the hub has started or failed (a
        // negotiate that never answers kept the grid loading).
        StateHasChanged();
        await ConnectToHub();
    }

    // The grid takes note of the rows it holds before the new list lands, so the alert that just
    // fired reads bold for a few seconds.
    private void OnRecentAlertsChanged() => InvokeAsync(async () =>
    {
        if (_recentGrid is not null) await _recentGrid.Refresh();
        _recent = AlertNotify.Recent;
        StateHasChanged();
    });

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
            var alerts = await Api.Monitoring.GetAlertRulesAsync();
            // Before the rows change, so a rule created elsewhere reads bold.
            if (_rulesGrid is not null) await _rulesGrid.Refresh();
            _alerts = alerts;
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

        if (result is true)
        {
            try { _alerts = await Api.Monitoring.GetAlertRulesAsync(); }
            catch (HttpRequestException) { /* expired JWT - redirect handled by AuthProvider */ }
            StateHasChanged();
        }
    }

    private async Task DeleteAlert(int id)
    {
        var confirmed = await Dialog.Confirm(L["DeleteConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Ui.RunAsync(
            () => Api.Monitoring.DeleteAlertRuleAsync(id),
            "Deleted",
            async () => _alerts = await Api.Monitoring.GetAlertRulesAsync(),
            errorKey: "DeleteFailed",
            successTitleKey: "Deleted");
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
    private static readonly Func<AlertRuleDto, object?> ThresholdExport = alert => $"{alert.Operator} {AlertThresholdUnit.Format(alert.Metric, alert.Threshold)}";
}
