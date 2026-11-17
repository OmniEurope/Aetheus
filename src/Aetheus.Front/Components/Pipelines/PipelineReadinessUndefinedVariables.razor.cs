// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The variables a project's pipelines read that nothing defines (R-56), as a table of the variable and the
/// library or vault to create it in, each a link to where it is done.</summary>
public partial class PipelineReadinessUndefinedVariables
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PipelineSetupReadinessUndefinedVariableDto> Rows { get; set; } = [];

    /// <summary>The project the holders belong to, for the creation of one that does not exist yet.</summary>
    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>Where the pages opened from a link send the user back; validated as local.</summary>
    [Parameter] public string? ReturnUrl { get; set; }

    /// <summary>R-24: raised after a holder was declared in a pipeline, so the host reads its readiness again.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;

    // The rows whose declaration is being written: the button says it works and does not run twice.
    private readonly HashSet<(int PipelineId, string Holder)> _declaring = [];

    /// <summary>R-24: the one-click declaration is offered to who may write the pipeline; anyone else keeps the link
    /// that opens it. Needs the pipeline the variable is read by.</summary>
    private bool CanDeclare(PipelineSetupReadinessUndefinedVariableDto row) =>
        row.PipelineId is { } id && Permissions.CanWrite(ResourceType.Pipeline, id);

    private bool IsDeclaring(PipelineSetupReadinessUndefinedVariableDto row) =>
        row.PipelineId is { } id && _declaring.Contains((id, row.ExistingHolderName));

    private string DeclareText(PipelineSetupReadinessUndefinedVariableDto row) =>
        string.Format(L["ReadinessDeclareHolder"].Value, row.ExistingHolderName, row.PipelineName);

    private async Task DeclareAsync(PipelineSetupReadinessUndefinedVariableDto row)
    {
        if (row.PipelineId is not { } pipelineId || !_declaring.Add((pipelineId, row.ExistingHolderName))) return;
        try
        {
            var isVault = ExistingIsVault(row);
            var confirmed = await Dialog.Confirm(
                string.Format(L[isVault ? "ReadinessDeclareVaultConfirm" : "ReadinessDeclareLibraryConfirm"].Value,
                    row.ExistingHolderName, row.PipelineName),
                L["ReadinessDeclareTitle"].Value,
                new OmniConfirmOptions { OkButtonText = L["ReadinessDeclareButton"].Value, CancelButtonText = L["GoBack"].Value });
            if (confirmed != true) return;

            var outcome = await Api.Pipelines.DeclarePipelineHolderAsync(pipelineId, new DeclarePipelineHolderRequest
            {
                Kind = row.ExistingHolderKind,
                Name = row.ExistingHolderName
            });
            if (outcome.Value is not null)
            {
                Toast.Success("ReadinessDeclaredTitle", isVault ? "ReadinessDeclaredVault" : "ReadinessDeclaredLibrary",
                    row.ExistingHolderName, row.PipelineName);
                await OnChanged.InvokeAsync();
            }
            else if (outcome.ErrorMessage is { Length: > 0 } refusal)
                Toast.Notify(OmniSeverity.Danger, "ReadinessDeclareFailed", refusal);
            else if (outcome.StatusCode == System.Net.HttpStatusCode.Forbidden)
                Toast.Error("ReadinessDeclareFailed", "ReadinessDeclareForbidden");
            else
                Toast.Error("ReadinessDeclareFailed", "SaveFailed");
        }
        finally
        {
            _declaring.Remove((pipelineId, row.ExistingHolderName));
        }
    }

    private IReadOnlyList<PipelineSetupReadinessUndefinedVariableDto> Ordered =>
        [.. Rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)];

    private static string KindKey(PipelineSetupReadinessUndefinedVariableDto row) =>
        row.Container == PipelineSetupReadinessContainerKind.Vault ? "Vault" : "VariableLibrary";

    private static bool ExistingIsVault(PipelineSetupReadinessUndefinedVariableDto row) =>
        row.ExistingHolderKind == PipelineSetupReadinessContainerKind.Vault;

    private static string ExistsKey(PipelineSetupReadinessUndefinedVariableDto row) =>
        ExistingIsVault(row) ? "ReadinessExistsInVault" : "ReadinessExistsInLibrary";

    // "ne le déclare pas" for a vault (le coffre), "ne la déclare pas" for a library (la bibliothèque).
    private static string NotDeclaredKey(PipelineSetupReadinessUndefinedVariableDto row) =>
        ExistingIsVault(row) ? "ReadinessNotDeclaredVault" : "ReadinessNotDeclaredLibrary";

    private static string DeclareKey(PipelineSetupReadinessUndefinedVariableDto row) =>
        ExistingIsVault(row) ? "ReadinessDeclareUnderVaults" : "ReadinessDeclareUnderLibraries";

    private static string CreateKey(PipelineSetupReadinessUndefinedVariableDto row) =>
        row.Container == PipelineSetupReadinessContainerKind.Vault ? "ReadinessCreateVaultForKey" : "ReadinessCreateLibraryForKey";

    private static string NoneKey(PipelineSetupReadinessUndefinedVariableDto row) =>
        row.Container == PipelineSetupReadinessContainerKind.Vault ? "ReadinessNoVaultDeclared" : "ReadinessNoLibraryDeclared";
}
