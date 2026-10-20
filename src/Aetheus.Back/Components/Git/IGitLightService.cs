// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Git;

public interface IGitLightService
{
    // Repository CRUD
    Task<List<GitLightRepoDto>> GetRepositoriesAsync(int projectId, CancellationToken ct = default);

    /// <summary>Cross-project repo list for the "all repositories" view. <paramref name="accessibleProjectIds"/>
    /// null = admin/unrestricted (every repo); a list restricts to those projects.</summary>
    Task<List<GitLightRepoDto>> GetAccessibleRepositoriesAsync(List<int>? accessibleProjectIds, CancellationToken ct = default);
    Task<PaginatedResult<GitLightRepoDto>> GetRepositoriesPageAsync(
        int? projectId, List<int>? accessibleProjectIds, PaginationRequest request,
        CancellationToken ct = default);

    Task<GitLightRepoDto?> GetRepositoryAsync(int id, CancellationToken ct = default);
    Task<GitLightRepoDto> CreateRepositoryAsync(CreateGitLightRepoRequest request, CancellationToken ct = default);
    Task EnsureRepositoryInitializedAsync(int id, CancellationToken ct = default);
    Task<GitLightRepoDto?> UpdateRepositoryAsync(int id, UpdateGitLightRepoRequest request, CancellationToken ct = default);
    Task<bool> DeleteRepositoryAsync(int id, CancellationToken ct = default);

    // Commits
    Task<PaginatedResult<GitLightCommitDto>> GetCommitsAsync(int repoId, string? refName, int page, int pageSize, string? search = null, CancellationToken ct = default, GitCommitLogFilter? filter = null);

    Task<GitLightCommitDetailDto?> GetCommitDetailAsync(int repoId, string sha, CancellationToken ct = default);
    Task<Dictionary<string, string>> GetCommitMessagesAsync(int repoId, IReadOnlyCollection<string> shas, CancellationToken ct = default);

    // Branches
    Task<List<GitLightBranchDto>> GetBranchesAsync(int repoId, CancellationToken ct = default);
    Task CreateBranchAsync(int repoId, CreateGitLightBranchRequest request, CancellationToken ct = default);
    Task DeleteBranchAsync(int repoId, string name, CancellationToken ct = default);

    // Tags
    Task<List<GitLightTagDto>> GetTagsAsync(int repoId, CancellationToken ct = default);
    Task CreateTagAsync(int repoId, CreateGitLightTagRequest request, CancellationToken ct = default);
    Task DeleteTagAsync(int repoId, string name, CancellationToken ct = default);

    // File browser
    Task<List<GitLightTreeEntryDto>> GetTreeAsync(int repoId, string? refName, string? path, CancellationToken ct = default);
    Task<GitLightBlobDto?> GetBlobAsync(int repoId, string refName, string path, CancellationToken ct = default);
    Task<Stream?> GetBlobStreamAsync(int repoId, string refName, string path, CancellationToken ct = default);

    // Internal Pull Requests
    Task<PaginatedResult<InternalPullRequestDto>> GetPullRequestsAsync(int repoId, PullRequestPaginationRequest request, CancellationToken ct = default);
    Task<InternalPullRequestDto?> GetPullRequestAsync(int repoId, int prId, CancellationToken ct = default);
    Task<InternalPullRequestDto> CreatePullRequestAsync(int repoId, CreateInternalPullRequestRequest request, string authorLogin, CancellationToken ct = default);
    Task<InternalPullRequestDto?> MergePullRequestAsync(int repoId, int prId, string authorLogin, CancellationToken ct = default);
    Task<InternalPullRequestDto?> ClosePullRequestAsync(int repoId, int prId, CancellationToken ct = default);
    Task<PullRequestDiffDto> GetPullRequestDiffAsync(int repoId, int prId, CancellationToken ct = default);

    // Helpers
    string ResolveDiskPath(int projectId, string slug);
    string BuildCloneUrl(int projectId, string slug);

    // Branch Protection
    Task<List<BranchProtectionRuleDto>> GetBranchProtectionRulesAsync(int repoId, CancellationToken ct = default);
    Task<BranchProtectionRuleDto> CreateBranchProtectionRuleAsync(int repoId, CreateBranchProtectionRuleRequest request, CancellationToken ct = default);
    Task<BranchProtectionRuleDto?> UpdateBranchProtectionRuleAsync(int repoId, int ruleId, UpdateBranchProtectionRuleRequest request, CancellationToken ct = default);
    /// <summary>Deletes a rule OF <paramref name="repoId"/>. A rule belonging to another repository
    /// is refused: the caller was authorized on the repository, not on the rule id.</summary>
    Task<bool> DeleteBranchProtectionRuleAsync(int repoId, int ruleId, CancellationToken ct = default);
    Task<bool> IsBranchProtectedAsync(int repoId, string branchName, CancellationToken ct = default);

    // Blame
    Task<List<GitLightBlameLine>> GetBlameAsync(int repoId, string refName, string path, CancellationToken ct = default);

    // Commit Graph
    Task<string> GetCommitGraphAsync(int repoId, int maxCount, CancellationToken ct = default);

    Task<List<GitLightCommitDto>> GetCommitGraphDataAsync(int repoId, int maxCount, CancellationToken ct = default);

    // RBAC helper: returns the owning project id for a repo, or null if it doesn't exist.
    Task<int?> GetProjectIdForRepoAsync(int repoId, CancellationToken ct = default);
}
