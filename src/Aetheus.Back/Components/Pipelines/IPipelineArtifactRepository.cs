// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Artifacts a run published, and the pipeline owning one. The port of the result submission service
/// (audit AUD-07). <see cref="IPipelineRepository"/> composes it and delegates to the same queries.
/// </summary>
public interface IPipelineArtifactRepository
{
    Task<List<PipelineArtifact>> GetArtifactsAsync(int runId, CancellationToken ct = default);

    Task AddArtifactAsync(PipelineArtifact artifact, CancellationToken ct = default);

    /// <summary>Returns the parent pipeline id for an artifact's run, or null if the artifact does not exist.</summary>
    Task<int?> GetPipelineIdForArtifactAsync(int artifactId, CancellationToken ct = default);
}
