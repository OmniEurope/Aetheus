// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

internal static class PipelineRunRepositoryResolver
{
    public static string CommitHref(int? repositoryId, CommitLinkDto commit) => repositoryId is { } id
        ? $"/git-repositories/{id}/commits/{Uri.EscapeDataString(commit.Sha)}"
        : $"/git-repositories/commits/{commit.Id}";

    public static string BranchHref(int? repositoryId, BranchLinkDto branch) => repositoryId is { } id
        ? $"/git-repositories/{id}?tab=branches&branch={Uri.EscapeDataString(branch.Name)}"
        : $"/git-repositories/branches/{branch.Id}";

    public static string BranchHref(int? repositoryId, int? projectId, string branchName) => repositoryId is { } id
        ? $"/git-repositories/{id}?tab=branches&branch={Uri.EscapeDataString(branchName)}"
        : $"/git-repositories?projectId={projectId}";

    /// <summary>Recette R-373: a run row's commit page in Aetheus, from the git graph when the run
    /// recorded the commit, else from the internal repository the backend resolved for the row. Null
    /// (text) otherwise: <see cref="PipelineRunDto.RepositoryUrl"/> of an internal repository is its
    /// smart-HTTP endpoint, which serves git, not a page.</summary>
    public static string? RunCommitHref(PipelineRunDto run) =>
        string.IsNullOrWhiteSpace(run.CommitHash) ? null
        : run.Commits.FirstOrDefault() is { } commit ? $"/git/commits/{commit.Id}"
        : run.RepositoryId is { } repositoryId ? $"/git-repositories/{repositoryId}/commits/{Uri.EscapeDataString(run.CommitHash)}"
        : null;

    /// <summary>Recette R-373: the branch counterpart of <see cref="RunCommitHref"/>.</summary>
    public static string? RunBranchHref(PipelineRunDto run) =>
        string.IsNullOrWhiteSpace(run.BranchName) ? null
        : run.Branches.FirstOrDefault() is { } branch ? $"/git/branches/{branch.Id}"
        : run.RepositoryId is { } repositoryId ? BranchHref(repositoryId, run.ProjectId, run.BranchName)
        : null;

    /// <summary>
    /// The Aetheus repository a run's source tile links to. FRONT-011: what the run recorded wins (the
    /// repository the backend resolved for it, else its <see cref="PipelineRunDto.RepositoryUrl"/>); the
    /// definition's current source is only the fallback of a run that recorded none, so a run made before
    /// the definition moved to another repository still opens the one it ran from.
    /// </summary>
    public static async Task<int?> ResolveAsync(
        ApiClient api,
        PipelineRunDto run,
        CancellationToken ct = default)
    {
        if (run.RepositoryId is { } recorded) return recorded;
        if (run.ProjectId is not { } projectId) return null;
        try
        {
            // Recette R-373: an internal clone URL is matched by project and slug, whatever host it
            // was re-homed to; any other URL by its canonical form. Never guessed among several.
            if (!string.IsNullOrWhiteSpace(run.RepositoryUrl))
                return GitRepositoryUrl.ResolveRepositoryId(run.RepositoryUrl, await api.Git.GetGitReposAsync(projectId, ct));

            var source = await api.Pipelines.GetPipelineSourceAsync(run.PipelineId, ct);
            return source is { RepositoryId: > 0 } ? source.RepositoryId : null;
        }
        catch (HttpRequestException) { return null; }
    }

}
