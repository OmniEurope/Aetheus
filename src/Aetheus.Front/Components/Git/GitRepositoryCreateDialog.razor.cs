// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Git;

public partial class GitRepositoryCreateDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public int ProjectId { get; set; }

    private CreateGitLightRepoRequest _request = new();
    private bool _submitting;

    protected override async Task OnInitializedAsync()
    {
        _request = new CreateGitLightRepoRequest { ProjectId = ProjectId };
        // Recette R3-60: a project's repository is named after the project unless the user says otherwise;
        // the backend derives the slug from the name.
        if (ProjectId <= 0) return;
        ProjectDetailDto? project;
        try { project = await Api.Projects.GetProjectDetailAsync(ProjectId); }
        catch (HttpRequestException) { return; }
        if (project is not null && string.IsNullOrWhiteSpace(_request.Name))
            _request.Name = project.Name;
    }

    private async Task Submit()
    {
        _submitting = true;
        var result = await Api.Git.CreateGitRepoAsync(_request);
        _submitting = false;

        if (result is not null)
        {
            Toast.Success("Created", result.Name);
            Dialog.Close(result.Id); // the id lets the caller open the repository page (R-31)
        }
    }

    private void Cancel() => Dialog.Close(false);

    /// <summary>
    /// R-31: the address of the page of the repository this dialog just created (<paramref name="dialogResult"/>
    /// is its id), carrying where to come back to: <paramref name="returnUrl"/> when it is local, else the
    /// page the user is on. Null when the dialog was cancelled.
    /// </summary>
    public static string? RepositoryPageHref(object? dialogResult, string? returnUrl, NavigationManager nav) =>
        dialogResult is int repositoryId
            ? LocalReturnUrl.Append($"/git-repositories/{repositoryId}", LocalReturnUrl.Validated(returnUrl) ?? LocalReturnUrl.Current(nav))
            : null;
}
