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

    private string TitleKey => IsVault ? "ReadinessEmptyVaultSecrets" : "ReadinessEmptyLibraryValues";

    private string OwnerTitleKey => IsVault ? "Vault" : "VariableLibrary";

    private string KeyTitleKey => IsVault ? "ReadinessColumnSecret" : "Parameter";
}
