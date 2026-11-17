// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Alerts;

// X4D8: dialog form for creating/editing an alert rule. Closes with `true` on success so the caller
// reloads the list; `false`/dismiss leaves the list untouched.
public partial class AlertEditDialog : EntityEditDialogBase
{
    /// <summary>The rule being edited; <c>null</c> opens the dialog in create mode.</summary>
    [Parameter] public AlertRuleDto? Alert { get; set; }

    private bool IsEdit => Alert is not null;
    private UpdateAlertRuleRequest _model = new();
    private List<OmniOption<MetricType>> _metricTypes = [];

    private List<OmniOption<AlertSeverity>> _severityTypes = [];
    private List<ServerDto> _servers = [];

    protected override async Task OnInitializedAsync()
    {
        _metricTypes = Enum.GetValues<MetricType>()
            .Select(metric => new OmniOption<MetricType>(metric, L.Localize(metric)))
            .ToList();
        _severityTypes = Enum.GetValues<AlertSeverity>()
            .Select(severity => new OmniOption<AlertSeverity>(severity, L.Localize(severity)))
            .ToList();
        if (Alert is { } a)
        {
            _model = new UpdateAlertRuleRequest
            {
                Name = a.Name,
                ServerId = a.ServerId,
                Metric = a.Metric,
                Operator = a.Operator,
                Threshold = a.Threshold,
                SustainedSeconds = a.SustainedSeconds,
                Severity = a.Severity,
                IsEnabled = a.IsEnabled,
                NotificationChannelId = a.NotificationChannelId
            };
        }

        try
        {
            _servers = await Api.Servers.GetAllServersAsync();
        }
        catch (HttpRequestException)
        {
            // The list stays empty: the required server field then refuses the save instead of creating a rule that never fires.
        }
    }

    // The list draws the saved rule at once (R-10): the dialog closes with what the server answered.
    private Task CloseWithSavedAsync(AlertRuleDto saved)
    {
        Dialog.Close(saved);
        return Task.CompletedTask;
    }

    private async Task SubmitAsync()
    {
        if (!ValidateRequiredName(_model.Name))
            return;

        // A rule without a server is excluded from metric evaluation and never fires: the server is mandatory.
        if (_model.ServerId is null)
        {
            Toast.Warning("ValidationError", "RequiredFields");
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (IsEdit)
            {
                await Ui.RunAsync(
                    () => Api.Monitoring.UpdateAlertRuleAsync(Alert!.Id, _model),
                    "AlertUpdated",
                    CloseWithSavedAsync,
                    successTitleKey: "Updated");
            }
            else
            {
                var create = new CreateAlertRuleRequest
                {
                    Name = _model.Name,
                    ServerId = _model.ServerId,
                    Metric = _model.Metric,
                    Operator = _model.Operator,
                    Threshold = _model.Threshold,
                    SustainedSeconds = _model.SustainedSeconds,
                    Severity = _model.Severity,
                    IsEnabled = _model.IsEnabled,
                    NotificationChannelId = _model.NotificationChannelId
                };
                await Ui.RunAsync(
                    () => Api.Monitoring.CreateAlertRuleAsync(create),
                    "AlertCreated",
                    CloseWithSavedAsync,
                    successTitleKey: "Created");
            }
        });
    }
}
