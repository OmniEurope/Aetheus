// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Git;

public partial class BranchDetail
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;

    [Parameter] public int BranchId { get; set; }

    private int? _loadedBranchId;
    private bool _loading;
    private int? _projectId;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedBranchId == BranchId) return;
        _loadedBranchId = BranchId;
        var branchId = BranchId;
        _loading = true;
        GitBranchDto? branch;
        try { branch = await Api.Git.GetGitBranchAsync(branchId); }
        catch (HttpRequestException) { branch = null; }
        if (BranchId != branchId) return;
        if (branch is null)
        {
            _loading = false;
            return;
        }

        _projectId = branch.ProjectId;
        ProjectNav.Set(branch.ProjectId);
        List<GitLightRepoDto> repositories;
        try { repositories = await Api.Git.GetGitReposAsync(branch.ProjectId); }
        catch (HttpRequestException) { repositories = []; }
        if (BranchId != branchId) return;

        var repositoryId = GitRepositorySelection.Resolve(repositories, branch.RepositoryUrl);
        var target = repositoryId is { } id
            ? $"/git-repositories/{id}?tab=branches&branch={Uri.EscapeDataString(branch.Name)}"
            : $"/git-repositories?projectId={branch.ProjectId}";
        Nav.NavigateTo(target, replace: true);
    }
}
