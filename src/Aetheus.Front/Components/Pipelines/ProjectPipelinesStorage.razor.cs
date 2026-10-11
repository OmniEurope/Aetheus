// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R1-34: a project's pipeline definitions are written to <c>.pipeline/</c> of its own internal
/// repository when it has one (the mirror of an external repository attached as an additional source
/// is not where a definition is written), and kept in Aetheus otherwise. No repository is created to
/// hold them.
/// </summary>
public partial class ProjectPipelinesStorage
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    private List<GitLightRepoDto>? _ownRepositories;
    private int? _loadedFor;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == ProjectId) return;
        _loadedFor = ProjectId;
        try
        {
            var repositories = await Api.Git.GetGitReposAsync(ProjectId);
            _ownRepositories = repositories
                .Where(repository => repository.ProjectId == ProjectId && !repository.IsAdditionalSource)
                .ToList();
        }
        catch (HttpRequestException)
        {
            // Unread: say nothing rather than a storage place the page does not know.
            _ownRepositories = null;
        }
    }
}
