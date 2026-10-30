// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Forms;

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineTemplates : IAsyncDisposable
{
    [Parameter] public bool Embedded { get; set; }

    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    private List<PipelineTemplateSummaryDto> _templates = [];
    private AetheusDataGrid<PipelineTemplateSummaryDto>? _grid;
    private static readonly IReadOnlyList<string> YamlContentTypes =
        ["application/x-yaml", "application/yaml", "text/yaml", "text/x-yaml", "text/plain"];
    private bool _loading;
    private bool _loadFailed;
    private bool _showImporter;
    private bool _canWrite;
    private HubConnection? _hubConnection;

    protected override async Task OnInitializedAsync()
    {
        if (!Embedded)
            Breadcrumb.Set(new BreadcrumbItem(L["PipelineTemplates"]));
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshPermission();
        await LoadTemplatesAsync();
        await StartRealtimeAsync();
    }

    private void OnPermissionsChanged()
    {
        RefreshPermission();
        _ = InvokeAsync(StateHasChanged);
    }

    private void RefreshPermission() => _canWrite = Permissions.CanWrite(ResourceType.PipelineTemplate);

    private async Task LoadTemplatesAsync()
    {
        if (_loading) return;
        _loading = true;
        _loadFailed = false;
        try { _templates = await Api.PipelineTemplates.GetPipelineTemplatesAsync(); }
        catch (HttpRequestException) { _templates = []; _loadFailed = true; }
        finally { _loading = false; }
    }

    private async Task StartRealtimeAsync()
    {
        try
        {
            _hubConnection = HubFactory.Create("entities");
            _hubConnection.On<ResourceType, int, string>("EntityChanged", (type, _, _) =>
                type == ResourceType.PipelineTemplate
                    ? InvokeAsync(ReloadFromRealtimeAsync)
                    : Task.CompletedTask);
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.PipelineTemplate);
                await ReloadFromRealtimeAsync();
            }));
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.PipelineTemplate);
        }
        catch { /* SignalR is best-effort; local mutations still reload explicitly. */ }
    }

    // A live change re-reads the list without the loader; the grid notes the rows it holds before
    // the new list lands, so a template created elsewhere reads bold for a few seconds. A failure
    // keeps the rows on screen for the next event.
    private async Task ReloadFromRealtimeAsync()
    {
        try
        {
            // FRONT-012: the event says the server list changed; the client cache must not answer for it.
            Api.PipelineTemplates.InvalidateTemplateCache();
            var templates = await Api.PipelineTemplates.GetPipelineTemplatesAsync();
            if (_grid is not null) await _grid.Refresh();
            _templates = templates;
            _loadFailed = false;
        }
        catch (HttpRequestException) { /* realtime reload - the next event or a user action retries */ }
        StateHasChanged();
    }

    private void CreateTemplate() => Nav.NavigateTo("/templates/new");
    private void EditTemplate(PipelineTemplateSummaryDto template) => Nav.NavigateTo($"/templates/{template.Id}");
    private void ViewHistory(PipelineTemplateSummaryDto template) => Nav.NavigateTo($"/templates/{template.Id}/versions");
    private void UseTemplate(PipelineTemplateSummaryDto template) =>
        Nav.NavigateTo(PipelineTemplateUseRoute.For(template.Id, template.Name));

    private async Task DeleteTemplateAsync(PipelineTemplateSummaryDto template)
    {
        var confirmed = await Dialog.Confirm(string.Format(L["DeleteConfirm"], template.Name), L["Delete"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"], CancelButtonText = L["GoBack"] });
        if (confirmed != true) return;
        await Veil.RunAsync(async () =>
        {
            if (await Api.PipelineTemplates.DeletePipelineTemplateAsync(template.Id))
            {
                Toast.Success("Deleted");
                await LoadTemplatesAsync();
            }
        });
    }

    private async Task ExportTemplateAsync(PipelineTemplateSummaryDto template)
    {
        var full = await Api.PipelineTemplates.GetPipelineTemplateAsync(template.Id);
        if (full is not null)
            await Js.InvokeVoidAsync("downloadFile", $"{full.Name}.yaml", full.YamlContent, "application/x-yaml");
    }

    private Task OnImportClickAsync()
    {
        _showImporter = !_showImporter;
        return Task.CompletedTask;
    }

    private async Task OnFileSelectedAsync(IReadOnlyList<IBrowserFile> files)
    {
        var file = files.Single();
        if (file.Size == 0 || file.Size > 1_048_576) return;
        using var stream = new MemoryStream();
        await file.OpenReadStream(1_048_576).CopyToAsync(stream);
        var result = await Api.PipelineTemplates.ImportPipelineTemplateAsync(stream.ToArray(), file.Name);
        if (result is null) return;
        _showImporter = false;
        Toast.Success("Imported");
        await LoadTemplatesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        if (_hubConnection is null) return;
        try { await _hubConnection.InvokeAsync("LeaveEntityUpdates", ResourceType.PipelineTemplate); }
        catch { /* best-effort */ }
        await _hubConnection.DisposeAsync();
        _hubConnection = null;
    }
}
