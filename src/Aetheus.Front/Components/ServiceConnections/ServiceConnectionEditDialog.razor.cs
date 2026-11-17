// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.ServiceConnections;

public partial class ServiceConnectionEditDialog : EntityEditDialogBase
{
    /// <summary>The connection being edited; <c>null</c> opens the dialog in create mode.</summary>
    [Parameter] public int? ConnectionId { get; set; }

    private bool IsEdit => ConnectionId is not null;
    private EditModel _model = new();
    private bool _loading;

    private List<OmniOption<ServiceConnectionType>> _types = [];

    protected override async Task OnInitializedAsync()
    {
        _types = Enum.GetValues<ServiceConnectionType>()
            .Select(type => new OmniOption<ServiceConnectionType>(type, L.Localize(type)))
            .ToList();
        if (ConnectionId is not int id) return;

        _loading = true;
        try
        {
            var detail = await Api.Settings.GetServiceConnectionAsync(id);
            if (detail is not null)
            {
                _model = new EditModel
                {
                    Name = detail.Name,
                    Description = detail.Description,
                    Type = detail.Type,
                    Url = detail.Url,
                    ConfigurationJson = detail.ConfigurationJson,
                    ProjectId = detail.ProjectId
                };
            }
        }
        catch (HttpRequestException) { /* 401 redirect handled upstream */ }
        finally { _loading = false; }
    }

    private async Task SubmitAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (IsEdit)
            {
                await Ui.RunAsync(
                    () => Api.Settings.UpdateServiceConnectionAsync(ConnectionId!.Value, new UpdateServiceConnectionRequest
                    {
                        Name = _model.Name,
                        Description = _model.Description,
                        ProjectId = _model.ProjectId,
                        Url = _model.Url,
                        ConfigurationJson = string.IsNullOrWhiteSpace(_model.ConfigurationJson) ? "{}" : _model.ConfigurationJson
                    }),
                    "Updated",
                    CloseWithSavedAsync,
                    successTitleKey: "Updated");
            }
            else
            {
                await Ui.RunAsync(
                    () => Api.Settings.CreateServiceConnectionAsync(new CreateServiceConnectionRequest
                    {
                        Name = _model.Name,
                        Description = _model.Description,
                        Type = _model.Type,
                        ProjectId = _model.ProjectId,
                        Url = _model.Url,
                        ConfigurationJson = string.IsNullOrWhiteSpace(_model.ConfigurationJson) ? "{}" : _model.ConfigurationJson
                    }),
                    "Created",
                    CloseWithSavedAsync,
                    successTitleKey: "Created");
            }
        });
    }

    // The list draws the saved connection at once (R-10): the dialog closes with what the server answered.
    private Task CloseWithSavedAsync(ServiceConnectionDto saved)
    {
        Dialog.Close(saved);
        return Task.CompletedTask;
    }

    // Local form model so create/edit share one binding surface (Update has no Type; Create does).
    private sealed class EditModel
    {
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Description { get; set; }

        public ServiceConnectionType Type { get; set; } = ServiceConnectionType.GitHub;

        public int? ProjectId { get; set; }

        [StringLength(500)]
        public string? Url { get; set; }

        [StringLength(8000)]
        public string ConfigurationJson { get; set; } = "{}";
    }
}
