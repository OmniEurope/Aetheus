// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineTemplateUpdate : PipelineFleetUpdateBase
{
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;

    private (int PipelineId, int TargetVersion)? _loadedKey;

    protected override Task OnParametersSetAsync()
    {
        var key = (PipelineId, TargetVersion);
        if (_loadedKey == key) return Task.CompletedTask;
        _loadedKey = key;
        _acknowledged = false;
        return LoadPreviewAsync();
    }

    protected override async Task OnPreviewLoadedAsync(PipelineFleetUpdatePreviewDto preview)
    {
        var pipeline = await Api.Pipelines.GetPipelineAsync(PipelineId).ConfigureAwait(false);
        PipelineActionBreadcrumbs.Set(Breadcrumb, L, PipelineId, pipeline, L["UpdatePipelineTemplate"]);
    }

    private async Task ConfirmAsync()
    {
        try
        {
            var updated = await ApplyUpdateAsync();
            if (updated is null)
            {
                Toast.Error("Error", "SaveFailed");
                return;
            }
            Toast.Success("Updated", updated.Name);
            Nav.NavigateTo($"/pipelines/{PipelineId}");
        }
        catch (HttpRequestException) { Toast.Error("Error", "SaveFailed"); }
    }

}
