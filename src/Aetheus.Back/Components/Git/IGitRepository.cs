// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Git;

public interface IGitRepository
{
    Task<List<GitConnection>> GetConnectionsByProjectAsync(int projectId, CancellationToken ct = default);
    Task<GitConnection?> GetConnectionDetailAsync(int id, CancellationToken ct = default);
    Task<GitConnection?> FindConnectionAsync(int id, CancellationToken ct = default);
    Task AddConnectionAsync(GitConnection connection, CancellationToken ct = default);
    Task RemoveConnectionAsync(GitConnection connection, CancellationToken ct = default);
    Task<(List<PullRequest> Items, int TotalCount)> GetPullRequestsPagedAsync(
        int gitConnectionId, string? search, int page, int pageSize,
        Aetheus.Shared.Components.Git.PullRequestStatus? status = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = true,
        IReadOnlyList<GridFilter>? columnFilters = null);

    /// <summary>The distinct authors of a connection's pull requests.</summary>
    Task<List<string>> GetPullRequestAuthorsAsync(int gitConnectionId, CancellationToken ct = default);
    Task<PullRequest?> FindPullRequestByExternalIdAsync(int gitConnectionId, int externalId, CancellationToken ct = default);
    Task AddPullRequestAsync(PullRequest pr, CancellationToken ct = default);
    Task<List<BranchPolicy>> GetBranchPoliciesAsync(int gitConnectionId, CancellationToken ct = default);
    Task<BranchPolicy?> FindBranchPolicyAsync(int id, CancellationToken ct = default);
    Task AddBranchPolicyAsync(BranchPolicy policy, CancellationToken ct = default);
    Task RemoveBranchPolicyAsync(BranchPolicy policy, CancellationToken ct = default);
    /// <summary>Own-read over PipelineRun; see the implementation for why it lives here.</summary>
    Task<int?> GetRunProjectIdAsync(int pipelineRunId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
