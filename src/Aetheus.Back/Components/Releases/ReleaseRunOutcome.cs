// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.CodeAnalysis;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Releases;

/// <summary>What the end of a run does to the release that points at it.</summary>
internal static class ReleaseRunOutcome
{
    /// <summary>Returns true when an earlier run deployed <paramref name="existing"/> and the
    /// current run reports it as not deployed. Preserve that live release when a later attempt
    /// rolls back.</summary>
    public static bool IsLiveBeforeThisRun([NotNullWhen(true)] Release? existing, bool deployed, int pipelineRunId) =>
        existing is { Status: ReleaseStatus.Deployed } && !deployed && existing.PipelineRunId != pipelineRunId;

    /// <summary>Preserve the recorded source commit during deployment or demotion because the
    /// current workspace may differ from the built release.</summary>
    public static bool KeepsRecordedCommit(Release existing, bool deployed) =>
        !string.IsNullOrWhiteSpace(existing.CommitHash)
        && (deployed || existing.Status == ReleaseStatus.Deployed);

    /// <summary>
    /// Writes the commit, tag and branch a run reports onto a release it records again; a value the
    /// run does not send leaves the recorded one. The commit follows <see cref="KeepsRecordedCommit"/>,
    /// so this runs before the release's status changes.
    /// </summary>
    public static void RecordSourceIdentity(
        Release existing, bool deployed, string? commitHash, string? tagName, string? branchName)
    {
        if (commitHash is not null && !KeepsRecordedCommit(existing, deployed)) existing.CommitHash = commitHash;
        if (tagName is not null) existing.TagName = tagName;
        if (branchName is not null) existing.BranchName = branchName;
    }

    public static bool Apply(Release release, PipelineStatus status, DateTime nowUtc)
    {
        if (status != PipelineStatus.Success && release.Status is not (ReleaseStatus.Detected or ReleaseStatus.Building))
            return false;

        release.Status = release.Status == ReleaseStatus.Deployed
            ? ReleaseStatus.Deployed
            : status == PipelineStatus.Success ? ReleaseStatus.Published : ReleaseStatus.Failed;
        if (release.Status == ReleaseStatus.Published)
            release.PublishedAt = nowUtc;
        return true;
    }
}
