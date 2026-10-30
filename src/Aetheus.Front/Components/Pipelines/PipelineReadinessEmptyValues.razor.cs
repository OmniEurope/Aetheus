// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The empty variables or secrets a project's pipelines need (R-10), as a table of library or
/// vault, key and pipeline, each a link to the page where it is fixed.</summary>
public partial class PipelineReadinessEmptyValues
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PipelineSetupReadinessEmptyValueDto> Rows { get; set; } = [];

    /// <summary>True for the secrets of the vaults, false for the entries of the variable libraries.</summary>
    [Parameter] public bool IsVault { get; set; }

    /// <summary>Where the pages opened from a link send the user back; validated as local.</summary>
    [Parameter] public string? ReturnUrl { get; set; }

    /// <summary>R-59: true in the setup wizard, where a click on a key opens a dialog to fill it and the
    /// user stays on the page.</summary>
    [Parameter] public bool FillInDialog { get; set; }

    /// <summary>Raised after a value was filled, so the host reads its readiness again.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    private string FillLabel(PipelineSetupReadinessEmptyValueDto row) =>
        string.Format(L["PipelineFillValueTitle"].Value, row.Key);

    private async Task FillAsync(PipelineSetupReadinessEmptyValueDto row)
    {
        var filled = await Dialog.OpenAsync<PipelineEmptyValueDialog>(
            string.Format(L["PipelineFillValueTitle"].Value, row.Key),
            new Dictionary<string, object?>
            {
                [nameof(PipelineEmptyValueDialog.OwnerId)] = row.OwnerId!.Value,
                [nameof(PipelineEmptyValueDialog.OwnerName)] = row.OwnerName,
                [nameof(PipelineEmptyValueDialog.EntryKey)] = row.Key,
                [nameof(PipelineEmptyValueDialog.IsVault)] = IsVault
            },
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });
        if (filled is true)
            await OnChanged.InvokeAsync();
    }

    private string TitleKey => IsVault ? "ReadinessEmptyVaultSecrets" : "ReadinessEmptyLibraryValues";

    private string OwnerTitleKey => IsVault ? "Vault" : "VariableLibrary";

    private string KeyTitleKey => IsVault ? "ReadinessColumnSecret" : "Parameter";
}
