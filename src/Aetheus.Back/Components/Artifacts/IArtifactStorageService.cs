// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Artifacts;

public interface IArtifactStorageService
{
    /// <summary>Streams the artifact to disk and returns its relative path plus the SHA-256 (lowercase hex) of the stored bytes.</summary>
    Task<(string RelativePath, string Sha256)> SaveArtifactAsync(int projectId, int pipelineId, int runId, string fileName, Stream content, CancellationToken ct = default);

    /// <summary>Moves an already verified file (an assembled chunked upload, kept beside the store)
    /// into the store under the same immutable name <see cref="SaveArtifactAsync"/> would give it,
    /// without reading it again. Returns the relative path.</summary>
    Task<string> AdoptVerifiedArtifactAsync(int projectId, int pipelineId, int runId, string fileName, VerifiedArtifactFile file, CancellationToken ct = default);

    Task DeleteArtifactAsync(string filePath, CancellationToken ct = default);
    Stream? OpenArtifact(string filePath);
    long GetArtifactSize(string filePath);
}
