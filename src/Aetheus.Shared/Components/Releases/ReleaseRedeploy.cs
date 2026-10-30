// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Releases;

/// <summary>
/// R-72: any release whose payload is still kept can be deployed again, whichever release is live, by
/// running the project's deployment pipeline with that version. The deployment pipeline and its gate stay the
/// authority (grade, approval, ancestry of the commit); this only keeps the screens from offering what the
/// pipeline would stop at the restore, for lack of an artifact.
/// </summary>
public static class ReleaseRedeploy
{
    /// <summary>The run parameter a deployment pipeline takes the version in.</summary>
    public const string VersionParameter = "candidateVersion";

    /// <summary>Whether the screens show the action at all: not for a release that was only detected, is
    /// still being built, or whose build failed, since none of them ever shipped a payload.</summary>
    public static bool IsOffered(ReleaseStatus status) =>
        status is not (ReleaseStatus.Detected or ReleaseStatus.Building or ReleaseStatus.Failed);

    /// <summary>Whether the release still keeps an artifact to deploy. An archived release lost them
    /// (decision D2), so it can only be run again from its commit.</summary>
    public static bool HasKeptArtifact(ReleaseDto release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return release.Artifacts.Count > 0;
    }

    /// <summary>The action is offered and enabled.</summary>
    public static bool CanRedeploy(ReleaseDto release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return IsOffered(release.Status) && HasKeptArtifact(release);
    }
}
