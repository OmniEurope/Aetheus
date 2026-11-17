// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// How a <c>restore-artifacts</c> step with a <c>release:</c> selector finds its artifact, and whether
/// a missing one may be skipped. One copy, shared by the dispatch
/// (<see cref="PipelineArtifactTaskFactory"/>) and the launch preflight
/// (<see cref="PipelineReleaseArtifactPreflight"/>), so the launch answers exactly what the step will.
/// </summary>
internal static class PipelineReleaseArtifactRules
{
    /// <summary>The selectors resolved relative to the run's own commit.</summary>
    internal static bool IsCommitRelative(string selector) =>
        string.Equals(selector, "previous-published", StringComparison.OrdinalIgnoreCase)
        || string.Equals(selector, "previous-deployed", StringComparison.OrdinalIgnoreCase);

    /// <summary>The selectors for which <c>allow_missing</c> may skip an absent artifact.</summary>
    internal static bool IsBootstrapSelector(string? selector) =>
        string.Equals(selector, "latest-published", StringComparison.OrdinalIgnoreCase)
        || string.Equals(selector, "previous-published", StringComparison.OrdinalIgnoreCase)
        || string.Equals(selector, "previous-deployed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(selector, "current-deployed", StringComparison.OrdinalIgnoreCase);

    internal static async Task<(PipelineArtifact? Artifact, PipelineProblem? Error)> FindAsync(
        IArtifactRepository artifacts,
        int projectId,
        string? commitHash,
        string selector,
        string? artifactName,
        CancellationToken ct)
    {
        var artifactFilter = string.IsNullOrWhiteSpace(artifactName) ? null : artifactName;
        PipelineArtifact? artifact;
        if (IsCommitRelative(selector))
        {
            if (!IsGitCommitHash(commitHash))
                return (null, PipelineArtifactProblems.NoVerifiedCommit());
            artifact = string.Equals(selector, "previous-deployed", StringComparison.OrdinalIgnoreCase)
                ? await artifacts.FindPreviousDeployedReleaseArtifactAsync(
                    projectId, commitHash!, artifactFilter, ct).ConfigureAwait(false)
                : await artifacts.FindPreviousPublishedReleaseArtifactAsync(
                    projectId, commitHash!, artifactFilter, ct).ConfigureAwait(false);
        }
        else
        {
            artifact = await artifacts.FindReleaseArtifactAsync(
                projectId, selector, artifactFilter, ct).ConfigureAwait(false);
        }
        return artifact is null
            ? (null, PipelineArtifactProblems.ReleaseNotRetained(selector))
            : (artifact, null);
    }

    /// <summary>Whether the release the selector names was sealed with a contract that requires the
    /// missing artifact, which turns an <c>allow_missing</c> skip into a failure.</summary>
    internal static async Task<bool> IsRequiredByPriorContractAsync(
        IArtifactRepository artifacts,
        string selector,
        int projectId,
        string? commitHash,
        string? artifactName,
        CancellationToken ct)
    {
        if (string.Equals(selector, "previous-published", StringComparison.OrdinalIgnoreCase))
            return IsGitCommitHash(commitHash)
                && await artifacts.RequiresPreviousPublishedArtifactAsync(
                    projectId, commitHash!, artifactName, ct).ConfigureAwait(false);
        if (string.Equals(selector, "previous-deployed", StringComparison.OrdinalIgnoreCase))
            return IsGitCommitHash(commitHash)
                && await artifacts.RequiresPreviousDeployedArtifactAsync(
                    projectId, commitHash!, artifactName, ct).ConfigureAwait(false);
        return string.Equals(selector, "current-deployed", StringComparison.OrdinalIgnoreCase)
            ? await artifacts.HasDeployedRollbackContractReleaseAsync(projectId, ct).ConfigureAwait(false)
            : await artifacts.HasPublishedRollbackContractReleaseAsync(projectId, ct).ConfigureAwait(false);
    }
}
