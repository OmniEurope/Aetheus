// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Records a commit Aetheus itself wrote into an internal repository (a pipeline definition saved
/// from the UI or the API). A push over smart HTTP and a mirror sync already keep the repository row
/// in step with the disk; a commit written by Aetheus must do the same, or the repository stays
/// flagged empty (recette R1-21) and every reader that skips empty repositories ignores it.
/// </summary>
public interface IGitCommitRecorder
{
    /// <summary>Clears the empty flag, stamps the last update and tells open views the repository changed.</summary>
    Task RecordCommitAsync(int repositoryId, CancellationToken ct = default);
}
