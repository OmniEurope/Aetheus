// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Git;

public partial class CommitDetail
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;

    [Parameter] public int CommitId { get; set; }

    private int? _loadedCommitId;
    private bool _loading;
    private int? _projectId;
    private GitCommitDto? _commit;

    /// <summary>The commit's page at its provider when no internal repository holds it.</summary>
    private string? ExternalHref => _commit is null ? null : ExternalCommitLink.For(_commit.RepositoryUrl, _commit.Sha);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedCommitId == CommitId) return;
        _loadedCommitId = CommitId;
        var commitId = CommitId;
        _loading = true;
        _commit = null;
        GitCommitDto? commit;
        try { commit = await Api.Git.GetGitCommitAsync(commitId); }
        catch (HttpRequestException) { commit = null; }
        if (CommitId != commitId) return;
        if (commit is null)
        {
            _loading = false;
            return;
        }

        _projectId = commit.ProjectId;
        ProjectNav.Set(commit.ProjectId);
        List<GitLightRepoDto> repositories;
        try { repositories = await Api.Git.GetGitReposAsync(commit.ProjectId); }
        catch (HttpRequestException) { repositories = []; }
        if (CommitId != commitId) return;

        if (GitRepositorySelection.Resolve(repositories, commit.RepositoryUrl) is { } id)
        {
            var holdsCommit = await RepositoryHoldsCommitAsync(id, commit.Sha);
            if (CommitId != commitId) return;
            if (holdsCommit)
            {
                Nav.NavigateTo($"/git-repositories/{id}/commits/{Uri.EscapeDataString(commit.Sha)}", replace: true);
                return;
            }
        }

        // No internal repository holds this commit (its runs cloned another one, an external
        // repository most often, or the graph names a commit the repository never received).
        // Say so, with its provider's page, instead of a dead link.
        _commit = commit;
        _loading = false;
    }

    /// <summary>
    /// Whether the repository's own history has the commit. The commit graph records the commits of runs and
    /// releases, which a repository need not hold; opening its page would answer 404, so the answer is asked
    /// from the endpoint that reports absent commits as an empty result. A SHA that is no hexadecimal id is
    /// held by no repository, and a failed question leaves the redirect to the commit page, which reports its own failure.
    /// </summary>
    private async Task<bool> RepositoryHoldsCommitAsync(int repositoryId, string sha)
    {
        if (!IsCommitId(sha)) return false;
        try
        {
            return (await Api.Git.GetGitCommitMessagesAsync(repositoryId, [sha])).Count > 0;
        }
        catch (HttpRequestException)
        {
            return true;
        }
    }

    private static bool IsCommitId(string sha) =>
        sha.Length is >= 4 and <= 64 && sha.All(char.IsAsciiHexDigit);
}
