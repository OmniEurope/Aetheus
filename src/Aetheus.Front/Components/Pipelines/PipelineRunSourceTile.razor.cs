// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunSourceTile
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunDto Run { get; set; } = default!;

    /// <summary>The run's repository when it resolved to one; links fall back to the project's list.</summary>
    [Parameter] public int? RepoId { get; set; }

    private string? RepositoryHref => RepoId is { } repoId
        ? $"/git-repositories/{repoId}"
        : Run.ProjectId is { } projectId ? $"/git-repositories?projectId={projectId}" : null;

    private string? CommitHref => Run.Commits.Count > 0
        ? PipelineRunRepositoryResolver.CommitHref(RepoId, Run.Commits[0])
        : !string.IsNullOrEmpty(Run.CommitHash)
            ? RepoId is { } commitRepoId
                ? $"/git-repositories/{commitRepoId}/commits/{Uri.EscapeDataString(Run.CommitHash)}"
                : Run.ProjectId is { } commitProjectId ? $"/git-repositories?projectId={commitProjectId}" : null
            : null;

    /// <summary>R-34: the release a deployment run delivers (null for any other run).</summary>
    private PipelineRunDeployedReleaseDto? DeployedRelease => Run.DeployedRelease;

    private string? DeployedCommitHref => DeployedRelease?.CommitHash is { Length: > 0 } sha
        ? RepoId is { } repositoryId
            ? $"/git-repositories/{repositoryId}/commits/{Uri.EscapeDataString(sha)}"
            : Run.ProjectId is { } projectId ? $"/git-repositories?projectId={projectId}" : null
        : null;

    private string? CommitSha => Run.Commits.Count > 0 ? Run.Commits[0].Sha : Run.CommitHash;

    private List<(string Name, string Href)> BranchLinks => Run.Branches.Count > 0
        ? [.. Run.Branches.Select(branch => (branch.Name, PipelineRunRepositoryResolver.BranchHref(RepoId, branch)))]
        : !string.IsNullOrEmpty(Run.BranchName)
            ? [(Run.BranchName, PipelineRunRepositoryResolver.BranchHref(RepoId, Run.ProjectId, Run.BranchName))]
            : [];

    private bool HasSourceFacts => RepositoryHref is not null || BranchLinks.Count > 0 || !string.IsNullOrEmpty(CommitSha);
}
