// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Shows the pipeline's owner, warns when it is inactive, and lets an administrator change
/// it.</summary>
public partial class PipelineOwnerPanel
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineDto Pipeline { get; set; } = default!;

    /// <summary>Raised after a transfer, so the page reloads the pipeline.</summary>
    [Parameter] public EventCallback OnOwnerChanged { get; set; }

    private async Task OnChangeOwnerAsync()
    {
        var result = await Dialog.OpenAsync<PipelineOwnerTransferDialog>(
            L["ChangePipelineOwner"].Value,
            new Dictionary<string, object?>
            {
                [nameof(PipelineOwnerTransferDialog.Scope)] = new PipelineOwnerTransferRequest { PipelineId = Pipeline.Id },
                [nameof(PipelineOwnerTransferDialog.Description)] = string.Format(
                    L["ChangePipelineOwnerDescription"].Value, Pipeline.Name, Pipeline.OwnerUsername ?? L["PipelineOwnerMissing"].Value),
                [nameof(PipelineOwnerTransferDialog.ProjectId)] = Pipeline.ProjectId,
                [nameof(PipelineOwnerTransferDialog.ProjectName)] = Pipeline.ProjectName,
            },
            new OmniDialogOptions { Width = "560px", AutoFocusFirstElement = false });
        if (result is PipelineOwnerTransferResultDto)
            await OnOwnerChanged.InvokeAsync();
    }
}
