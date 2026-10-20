// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Artifacts;

/// <summary>
/// How the expiry cleanup and the quota eviction remove an artifact: the row first, committed, then
/// its file. A refused row delete (a foreign key, a lost connection) therefore leaves both in place,
/// and the next pass retries; a refused file delete leaves only a file that no row references, which
/// the unreferenced-file sweep reclaims. The reverse order lost the file of a row that then stayed,
/// so a restore or download of it failed with a 404.
/// </summary>
internal static class ArtifactPayloadRemoval
{
    /// <summary>Removes the row, then the file. Returns whether this call removed the row; throws
    /// when the row delete is refused, with the file untouched.</summary>
    internal static async Task<bool> RemoveRowThenFileAsync(
        IArtifactRepository repo,
        IArtifactStorageService storage,
        PipelineArtifact artifact,
        ILogger logger,
        CancellationToken ct)
    {
        var removed = await repo.RemoveAsync(artifact, ct).ConfigureAwait(false);
        try
        {
            await storage.DeleteArtifactAsync(artifact.FilePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory: the row is already gone, so the file is unreferenced; the sweep of
            // unreferenced files deletes it later. Failing here would only hide the removal.
            logger.LogWarning(ex,
                "Artifact {ArtifactId}: row removed but its file {Path} could not be deleted; the unreferenced-file sweep will reclaim it",
                artifact.Id, artifact.FilePath);
        }
        return removed;
    }
}
