// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Git;

/// <summary> The export of each list of a repository page (commits, branches, tags, files, pull
/// requests, protection rules) reads its pages here, with the sort and column filters of its grid
/// and the same query the grid's own load sends, without the rows, the cache or the error state the
/// page keeps. A page that cannot be read throws, so the export fails instead of writing a partial
/// file. </summary>
internal sealed class GitRepositoryExportReader(GitApi git)
{
    public Task<PaginatedResult<GitLightCommitDto>> CommitsAsync(int repoId, GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(30);
        return git.GetGitCommitsAsync(repoId, GitReference.AllBranches, page, pageSize, search: null, args.ToApiFilters());
    }

    public Task<PaginatedResult<GitLightBranchDto>> BranchesAsync(int repoId, string? search, GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(GitLightBranchDto.Name));
        return git.GetGitBranchesPageAsync(repoId, page, pageSize, search, sortBy, sortDescending, filters: args.ToApiFilters());
    }

    public Task<PaginatedResult<GitLightTagDto>> TagsAsync(int repoId, GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(GitLightTagDto.Name));
        return git.GetGitTagsPageAsync(repoId, page, pageSize, search: null, sortBy, sortDescending, filters: args.ToApiFilters());
    }

    public Task<PaginatedResult<GitLightTreeEntryDto>> TreeAsync(int repoId, string? treeRef, string? treePath, GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(GitLightTreeEntryDto.Name));
        return git.GetGitTreePageAsync(repoId, treeRef, treePath, page, pageSize, search: null, sortBy, sortDescending,
            filters: args.ToApiFilters());
    }

    public Task<PaginatedResult<InternalPullRequestDto>> PullRequestsAsync(int repoId, GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(InternalPullRequestDto.CreatedAt), fallbackDescending: true);
        return git.GetGitPullRequestsAsync(repoId, page, pageSize, search: null, status: null, sortBy, sortDescending,
            args.ToApiFilters());
    }

    public Task<PaginatedResult<BranchProtectionRuleDto>> ProtectionRulesAsync(int repoId, GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(BranchProtectionRuleDto.Pattern));
        return git.GetGitBranchProtectionRulesPageAsync(repoId, page, pageSize, search: null, sortBy, sortDescending,
            filters: args.ToApiFilters());
    }
}
