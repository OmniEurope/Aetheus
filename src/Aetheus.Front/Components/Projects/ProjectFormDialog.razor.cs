// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects;

public partial class ProjectFormDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>Existing project to edit; null opens the dialog in create mode.</summary>
    [Parameter] public ProjectDetailDto? Project { get; set; }

    private ProjectFormModel _model = new();
    private bool _saving;
    private List<OmniOption<ProjectStatus>> _statusOptions = [];
    private bool _isNew => Project is null;

    /// <summary>Shared dialog options for opening the project form (create or edit).</summary>
    public static OmniDialogOptions OmniDialogOptions() => new()
    {
        Width = "min(920px, 94vw)",
        CloseDialogOnOverlayClick = false,
        ShowClose = true
    };

    protected override void OnInitialized()
    {
        _statusOptions = ProjectFormModel.BuildStatusOptions(L);

        if (Project is not null)
        {
            _model = ProjectFormModel.From(Project);
        }
    }

    private async Task OnSubmit()
    {
        _saving = true;
        var result = _isNew
            ? await Api.Projects.CreateProjectAsync(_model.ToCreateRequest())
            : await Api.Projects.UpdateProjectAsync(Project!.Id, _model.ToUpdateRequest());
        _saving = false;

        if (result is not null)
        {
            Toast.Success(_isNew ? "Created" : "Saved", _isNew ? "ProjectCreated" : "ProjectUpdated");
            Dialog.Close(result);
        }
        else
        {
            Toast.Error("Error", "SaveFailed");
        }
    }

    private async Task OnDelete()
    {
        if (Project is null) return;

        // The dialog counts what goes with the project, asks for its name and deletes it.
        if (!await ProjectDeleteDialog.OpenAsync(Dialog, L, Project.Id)) return;
        Dialog.Close();
        Nav.NavigateTo("/projects");
    }

}
