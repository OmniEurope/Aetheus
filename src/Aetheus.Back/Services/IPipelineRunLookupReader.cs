// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services;

/// <summary>
/// Narrow read port resolving which pipeline a run belongs to and where runs stand. It lets a caller
/// that only needs these lookups (the SignalR <c>PipelineHub</c>, the approval and result submission
/// services) work on a run without depending on the whole <see cref="IPipelineRepository"/> contract
/// (audit AUD-07).
/// </summary>
public interface IPipelineRunLookupReader
{
    /// <summary>The pipeline owning <paramref name="runId"/>, or null for an unknown run.</summary>
    Task<int?> GetPipelineIdForRunAsync(int runId, CancellationToken ct = default);

    /// <summary>Terminal/current status of the given runs, for cheaply reconciling waiting trigger steps.</summary>
    Task<Dictionary<int, PipelineStatus>> GetRunStatusesByIdsAsync(IReadOnlyCollection<int> runIds, CancellationToken ct = default);

    /// <summary>R-34: the release a deployment run delivers (the candidate it restores) beside the source head the
    /// run checked out; null when the run names no release this project has recorded. With
    /// <paramref name="includeAlreadyDeployed"/> the release already being in production is read too, for a run
    /// still waiting for its approval.</summary>
    Task<PipelineRunDeployedReleaseDto?> GetDeployedReleaseAsync(
        int runId, bool includeAlreadyDeployed, CancellationToken ct = default);
}
