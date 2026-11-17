// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

// ADR-021: dialog form for creating/editing a monitored app. Closes with `true` on success so the
// caller reloads the list; `false`/dismiss leaves it untouched.
public partial class MonitoredAppFormDialog : EntityEditDialogBase
{
    [Parameter] public int ProjectId { get; set; }

    /// <summary>The app being edited; <c>null</c> opens the dialog in create mode.</summary>
    [Parameter] public MonitoredAppDto? App { get; set; }

    private bool IsEdit => App is not null;
    private UpdateMonitoredAppRequest _model = new();
    private List<ServerDto> _servers = [];
    private List<EnvironmentDto> _environments = [];

    protected override async Task OnInitializedAsync()
    {
        if (App is { } a)
        {
            _model = new UpdateMonitoredAppRequest
            {
                Name = a.Name,
                EnvironmentId = a.EnvironmentId,
                ServerId = a.ServerId,
                ProbeUrl = a.ProbeUrl,
                ProbeIntervalSeconds = a.ProbeIntervalSeconds,
                ProbeTimeoutSeconds = a.ProbeTimeoutSeconds,
                ExpectedStatusCode = a.ExpectedStatusCode,
                FailureThreshold = a.FailureThreshold,
                RecoveryThreshold = a.RecoveryThreshold,
                Enabled = a.Enabled
            };
        }

        try
        {
            var serversTask = Api.Servers.GetAllServersAsync();
            var environmentsTask = Api.Servers.GetAllEnvironmentsAsync(projectId: ProjectId);
            await Task.WhenAll(serversTask, environmentsTask);
            _servers = await serversTask;
            _environments = await environmentsTask;
        }
        catch (HttpRequestException)
        {
            // Dropdown lookups are best-effort; the form still works with free-form defaults.
        }
    }

    private async Task SubmitAsync()
    {
        if (!ValidateRequiredName(_model.Name))
            return;

        await RunBusyAsync(async () =>
        {
            if (IsEdit)
            {
                await Ui.RunAsync(
                    () => Api.Monitoring.UpdateMonitoredAppAsync(App!.Id, _model),
                    "MonitoredAppUpdated",
                    saved => CloseAfterSuccessAsync(saved),
                    successTitleKey: "Updated");
            }
            else
            {
                var create = new CreateMonitoredAppRequest
                {
                    Name = _model.Name,
                    ProjectId = ProjectId,
                    EnvironmentId = _model.EnvironmentId,
                    ServerId = _model.ServerId,
                    ProbeUrl = _model.ProbeUrl,
                    ProbeIntervalSeconds = _model.ProbeIntervalSeconds,
                    ProbeTimeoutSeconds = _model.ProbeTimeoutSeconds,
                    ExpectedStatusCode = _model.ExpectedStatusCode,
                    FailureThreshold = _model.FailureThreshold,
                    RecoveryThreshold = _model.RecoveryThreshold,
                    Enabled = _model.Enabled
                };
                await Ui.RunAsync(
                    () => Api.Monitoring.CreateMonitoredAppAsync(ProjectId, create),
                    "MonitoredAppCreated",
                    saved => CloseAfterSuccessAsync(saved),
                    successTitleKey: "Created");
            }
        });
    }
}
