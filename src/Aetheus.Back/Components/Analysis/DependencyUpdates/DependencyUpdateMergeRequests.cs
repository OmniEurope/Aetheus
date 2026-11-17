// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Git;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>What retiring an update's branch gave: whether the branch was there to delete, or why it was kept.</summary>
internal sealed record DependencyUpdateRetirement(bool Existed, string? Refusal);

/// <summary>
/// R-30: the pull request and branch side of an update, on an internal repository only. The platform's own pull requests
/// (<see cref="IGitLightService"/>) are the one representation of a merge request: a repository that mirrors an external host never goes
/// through here, the caller announces what has to be done on its origin instead.
/// </summary>
internal interface IDependencyUpdateMergeRequests
{
    Task<CveRemediationRepositoryRow?> FindRepositoryAsync(int repositoryId, int projectId, CancellationToken ct);

    /// <summary>Opens the pull request from the update's branch into the default branch, or returns the one already open from that branch.
    /// Returns the row id of the pull request. Merging stays human.</summary>
    Task<int> OpenAsync(GitInternalRepo repository, string branch, string title, string description, CancellationToken ct);

    /// <summary>Closes the update's pull request when it is still open, then deletes the branch. A branch already gone is not an error; a
    /// branch a protection rule keeps, or that is not an update branch, is kept and the reason returned.</summary>
    Task<DependencyUpdateRetirement> RetireAsync(GitInternalRepo repository, string branch, int? pullRequestId, CancellationToken ct);
}

internal sealed class DependencyUpdateMergeRequests(
    CveRemediationRepository remediations, IGitLightService light, IGitLightCliService cli) : IDependencyUpdateMergeRequests
{
    public Task<CveRemediationRepositoryRow?> FindRepositoryAsync(int repositoryId, int projectId, CancellationToken ct) =>
        remediations.FindRepositoryAsync(repositoryId, projectId, ct);

    public async Task<int> OpenAsync(GitInternalRepo repository, string branch, string title, string description, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var target = repository.DefaultBranch;
        if (repository.GitConnectionId is { } connection
            && await remediations.FindOpenPullRequestAsync(connection, branch, target, ct).ConfigureAwait(false) is { } open)
            return open.Id;
        var created = await light.CreatePullRequestAsync(
            repository.Id,
            new CreateInternalPullRequestRequest { SourceBranch = branch, TargetBranch = target, Title = title, Description = description },
            DependencyUpdateGit.Author, ct).ConfigureAwait(false);
        return created.Id;
    }

    public async Task<DependencyUpdateRetirement> RetireAsync(
        GitInternalRepo repository, string branch, int? pullRequestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (!DependencyUpdateBranch.IsOurs(branch) || string.Equals(branch, repository.DefaultBranch, StringComparison.Ordinal))
            return new DependencyUpdateRetirement(true, $"'{branch}' is not a branch an update may delete.");

        // A pull request left open on a branch about to vanish would only dangle; a merged or closed one is left as it is.
        if (pullRequestId is { } id && await remediations.GetPullRequestAsync(id, ct).ConfigureAwait(false) is { Status: PullRequestStatus.Open } open)
            await light.ClosePullRequestAsync(repository.Id, open.Number, ct).ConfigureAwait(false);

        var diskPath = light.ResolveDiskPath(repository.ProjectId, repository.Slug);
        var branches = await cli.GetBranchesAsync(diskPath, repository.DefaultBranch, ct).ConfigureAwait(false);
        if (!branches.Exists(candidate => candidate.Name == branch)) return new DependencyUpdateRetirement(false, null);
        try
        {
            await light.DeleteBranchAsync(repository.Id, branch, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException refusal)
        {
            // Mandatory catch: the Git service refuses a protected branch (and a failed git command) by throwing; the branch stays and the
            // update says why instead of failing the pass.
            return new DependencyUpdateRetirement(true, refusal.Message);
        }

        return new DependencyUpdateRetirement(true, null);
    }
}
