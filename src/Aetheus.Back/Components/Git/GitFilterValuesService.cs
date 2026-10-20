// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Git;

/// <summary> The values the git grids' checkable column filters offer. The grids are loaded page by
/// page, so the candidates are read across the whole scope: the default branches of the
/// repositories the caller can read, and, for one repository, the authors of its recent commits and
/// of its pull requests. </summary>
public sealed class GitFilterValuesService(
    IGitLightRepository lightRepo,
    IGitRepository gitRepo,
    IGitLightCliService cli,
    IGitLightService light)
{
    public async Task<GitRepositoryFilterValuesDto> ForRepositoriesAsync(
        int? projectId, List<int>? accessibleProjectIds, CancellationToken ct = default)
    {
        if (accessibleProjectIds is { Count: 0 }) return new GitRepositoryFilterValuesDto();
        var branches = await lightRepo.GetDefaultBranchesAsync(accessibleProjectIds, projectId, ct).ConfigureAwait(false);
        return new GitRepositoryFilterValuesDto
        {
            DefaultBranches = [.. branches.Where(branch => !string.IsNullOrWhiteSpace(branch))]
        };
    }

    public async Task<GitRepositoryDetailFilterValuesDto> ForRepositoryAsync(int repoId, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return new GitRepositoryDetailFilterValuesDto();
        var diskPath = light.ResolveDiskPath(entity.ProjectId, entity.Slug);
        var commitAuthors = await cli.GetCommitAuthorsAsync(diskPath, ct).ConfigureAwait(false);
        // Every branch, not the first block the Branches tab shows.
        var branches = await cli.GetBranchesAsync(diskPath, entity.DefaultBranch, ct).ConfigureAwait(false);
        var prAuthors = entity.GitConnectionId is { } connectionId
            ? await gitRepo.GetPullRequestAuthorsAsync(connectionId, ct).ConfigureAwait(false)
            : [];
        return new GitRepositoryDetailFilterValuesDto
        {
            CommitAuthors = commitAuthors,
            CommitBranches = [.. branches.Select(branch => branch.Name).Order(StringComparer.OrdinalIgnoreCase)],
            PullRequestAuthors = prAuthors
        };
    }
}
