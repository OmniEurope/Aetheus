// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Projects;

/// <summary> Recette R-51 (ADR-058): what a project "deleted, to clean up" shows instead of its pages. The only
/// action is relaunching the cleanup, in the deletion dialog, which goes back to the projects list at its end. </summary>
public partial class ProjectDeletionPendingPanel
{
    [Parameter, EditorRequired] public int ProjectId { get; set; }

    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private Task RelaunchAsync() => ProjectDeleteDialog.OpenAsync(Dialog, L, ProjectId);
}
