// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Projects;

/// <summary> The confirmation of a project deletion. It shows what the deletion removes, read from
/// the server's preview, refuses while a run of the project is in progress, and enables Delete only
/// once the project's name is typed exactly. Closes with <c>true</c> once the project is deleted; a
/// refusal stays open, the server's reason being shown by the global error notification. </summary>
public partial class ProjectDeleteDialog
{
    [Parameter, EditorRequired] public int ProjectId { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private ProjectDeletionPreviewDto? _preview;
    private bool _loading = true;
    private bool _deleting;
    private string _confirmation = string.Empty;

    /// <summary>Opens the dialog; true once the project is deleted.</summary>
    internal static async Task<bool> OpenAsync(OmniDialogService dialog, IStringLocalizer<AppStrings> localizer, int projectId)
    {
        var result = await dialog.OpenAsync<ProjectDeleteDialog>(
            localizer["ProjectDeletionTitle"].Value,
            new Dictionary<string, object?> { [nameof(ProjectId)] = projectId },
            new OmniDialogOptions { Width = "640px", AutoFocusFirstElement = false });
        return result is true;
    }

    private bool CanDelete => _preview is { ActiveRunCount: 0 } preview
        && !_deleting
        && string.Equals(_confirmation.Trim(), preview.ProjectName, StringComparison.Ordinal);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _preview = await Api.Projects.GetDeletionPreviewAsync(ProjectId);
        }
        catch (HttpRequestException)
        {
            _preview = null;
        }
        _loading = false;
    }

    private async Task DeleteAsync()
    {
        if (!CanDelete) return;
        _deleting = true;
        var deleted = await Api.Projects.DeleteProjectAsync(ProjectId);
        _deleting = false;
        if (!deleted) return;
        Toast.Success("Deleted", "Deleted");
        Dialog.Close(true);
    }
}
