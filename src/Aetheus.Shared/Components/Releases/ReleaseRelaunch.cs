// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Releases;

/// <summary>
/// Decision D2: an archived release keeps its commit so that a complete pipeline can run again from it.
/// The backend refuses the relaunch and the front disables it under the same rule: the release is
/// archived and its commit is a full SHA-1 or SHA-256, the only form a run can be pinned to.
/// </summary>
public static class ReleaseRelaunch
{
    public static bool CanRelaunch(ReleaseStatus status, string? commitHash) =>
        status == ReleaseStatus.Archived && IsFullCommitHash(commitHash);

    public static bool IsFullCommitHash(string? commitHash) =>
        commitHash is { Length: 40 or 64 } && commitHash.All(Uri.IsHexDigit);
}
