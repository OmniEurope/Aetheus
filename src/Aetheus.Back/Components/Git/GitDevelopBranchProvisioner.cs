// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Git;

/// <summary>Cuts the <c>develop</c> branch of an internal repository from its default branch, for an organization that
/// asked for it (<c>Organization.AutoCreateDevelopBranch</c>, R-76, owner decision 2026-10-28).</summary>
public interface IGitDevelopBranchProvisioner
{
    /// <summary>Creates <c>develop</c> from the default branch of the repository when the organization of its project asks for
    /// it, the repository is the project's own source (neither a mirror of an external repository nor the configuration
    /// repository), the default branch has a commit and <c>develop</c> does not exist. Called once, when the repository gets
    /// its first commit: a repository has no branch to cut from before that, and calling it later would bring back a
    /// <c>develop</c> that someone deleted on purpose. Returns whether the branch was created.</summary>
    Task<bool> EnsureAfterFirstCommitAsync(int repositoryId, CancellationToken ct = default);
}

/// <inheritdoc cref="IGitDevelopBranchProvisioner"/>
public sealed class GitDevelopBranchProvisioner(
    IGitLightRepository lightRepo,
    IGitLightService git,
    ILogger<GitDevelopBranchProvisioner> logger) : IGitDevelopBranchProvisioner
{
    internal const string BranchName = "develop";

    public async Task<bool> EnsureAfterFirstCommitAsync(int repositoryId, CancellationToken ct = default)
    {
        try
        {
            var repository = await lightRepo.FindByIdWithProjectAsync(repositoryId, ct).ConfigureAwait(false);
            if (repository is null || repository.IsConfiguration || GitLightMapper.IsAdditionalSource(repository)) return false;
            if (!await lightRepo.IsDevelopBranchAutoCreationEnabledAsync(repository.ProjectId, ct).ConfigureAwait(false))
                return false;

            var branches = await git.GetBranchesAsync(repositoryId, ct).ConfigureAwait(false);
            if (branches.Exists(branch => string.Equals(branch.Name, BranchName, StringComparison.Ordinal))
                || !branches.Exists(branch => string.Equals(branch.Name, repository.DefaultBranch, StringComparison.Ordinal)))
                return false;

            await git.CreateBranchAsync(
                repositoryId, new CreateGitLightBranchRequest { Name = BranchName, StartRef = repository.DefaultBranch }, ct)
                .ConfigureAwait(false);
            logger.LogInformation(
                "Created branch {Branch} from {DefaultBranch} in repository {RepositoryId} after its first commit",
                BranchName, repository.DefaultBranch, repositoryId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory: the commit or push that triggered this is already stored and acknowledged. Failing to cut
            // a convenience branch must not turn it into a reported failure; the branch can be created by hand.
            logger.LogWarning(ex, "Could not create branch {Branch} in repository {RepositoryId}", BranchName, repositoryId);
            return false;
        }
    }
}
