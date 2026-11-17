// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyBump;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>What writing an update's branch gave: the commit the branch now points at, or the reason it was not written.</summary>
internal sealed record DependencyUpdateWrite(string? CommitSha, string? Error);

/// <summary>What launching a pipeline on an update's branch gave: the run, or the reason there is none.</summary>
internal sealed record DependencyUpdateLaunch(int? RunId, string? Refusal);

/// <summary>The repository side of an update: the remediation's own reading, planning and writing (<see cref="CveRemediationGit"/>),
/// so an update writes exactly as a remediation does, through the platform's git service only.</summary>
internal interface IDependencyUpdateGit
{
    Task<CveRemediationRepositoryRow?> FindRepositoryAsync(int repositoryId, int projectId, CancellationToken ct);

    Task<CveRemediationPlan> PlanAsync(int repositoryId, string baseBranch, DependencyBumpRequest request, CancellationToken ct);

    Task<DependencyUpdateWrite> WriteAsync(
        GitInternalRepo repository, string branch, string baseCommit, IReadOnlyList<(string Path, string Content)> files, string message,
        CancellationToken ct);
}

/// <summary>The pipeline side of an update: an automated launch on the branch, at the exact commit written.</summary>
internal interface IDependencyUpdatePipelines
{
    Task<DependencyUpdateLaunch> LaunchAsync(int pipelineId, string branch, string commit, int updateId, CancellationToken ct);
}

internal sealed class DependencyUpdateGit(
    CveRemediationGit git, CveRemediationRepository remediations, IGitLightService light, IGitLightCliService cli) : IDependencyUpdateGit
{
    /// <summary>The login and author name the platform writes updates under.</summary>
    public const string Author = "aetheus-deps";

    public Task<CveRemediationRepositoryRow?> FindRepositoryAsync(int repositoryId, int projectId, CancellationToken ct) =>
        remediations.FindRepositoryAsync(repositoryId, projectId, ct);

    public Task<CveRemediationPlan> PlanAsync(int repositoryId, string baseBranch, DependencyBumpRequest request, CancellationToken ct) =>
        git.PlanAsync(repositoryId, baseBranch, request, ct);

    public async Task<DependencyUpdateWrite> WriteAsync(
        GitInternalRepo repository, string branch, string baseCommit, IReadOnlyList<(string Path, string Content)> files, string message,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (await git.PushAsync(repository, branch, baseCommit, files, message, Author, ct).ConfigureAwait(false) is { } error)
            return new DependencyUpdateWrite(null, error);
        var commits = await cli.GetCommitsAsync(light.ResolveDiskPath(repository.ProjectId, repository.Slug), branch, 0, 1, ct: ct)
            .ConfigureAwait(false);
        return commits.Count == 0
            ? new DependencyUpdateWrite(null, $"The branch {branch} was written but its commit cannot be read back.")
            : new DependencyUpdateWrite(commits[0].Sha, null);
    }
}

/// <summary>Launches through the automated path every webhook and schedule uses (<see cref="IPipelineRunService.TriggerAutomatedRunAsync"/>):
/// authorized against the pipeline's owner, the refusal recorded as a failed run of the pipeline. The branch and the commit are the
/// update's, never the pipeline's own source branch.</summary>
internal sealed class DependencyUpdatePipelines(IPipelineRunService runs) : IDependencyUpdatePipelines
{
    public const string TriggerSource = "DependencyUpdate";

    /// <summary>The run variable naming the update a run tests.</summary>
    public const string UpdateVariable = "AETHEUS_DEPENDENCY_UPDATE";

    public async Task<DependencyUpdateLaunch> LaunchAsync(int pipelineId, string branch, string commit, int updateId, CancellationToken ct)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineRunService.SourceBranchVariable] = branch,
            [PipelineRunService.SourceCommitVariable] = commit,
            [UpdateVariable] = updateId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        try
        {
            var run = await runs.TriggerAutomatedRunAsync(pipelineId, TriggerSource, variables, ct).ConfigureAwait(false);
            return run is null
                ? new DependencyUpdateLaunch(null, "The run was not authorized for the pipeline's owner; its runs list says why.")
                : new DependencyUpdateLaunch(run.Id, null);
        }
        catch (BadRequestException refusal)
        {
            // Mandatory catch: an automated launch refuses by throwing, after recording a failed run that carries the reason. The update
            // keeps its branch and says why the run did not start, and the other pipeline still gets its launch.
            return new DependencyUpdateLaunch(null, refusal.Message);
        }
    }
}
