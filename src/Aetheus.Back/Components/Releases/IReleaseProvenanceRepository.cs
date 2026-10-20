// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Releases;

/// <summary>Read-only queries behind the release provenance view.</summary>
public interface IReleaseProvenanceRepository
{
    Task<ReleaseProvenanceFacts?> GetFactsAsync(int releaseId, CancellationToken ct = default);

    /// <summary>Runs of the project started by a <c>trigger</c> step of the given runs (one level).</summary>
    Task<List<int>> GetTriggeredRunIdsAsync(
        int projectId, IReadOnlyCollection<int> runIds, CancellationToken ct = default);

    /// <summary>Runs that consumed the release: a deploy that selected it, or any step that took one of
    /// its artifacts.</summary>
    Task<List<ReleaseArtifactUseFact>> GetArtifactUsesAsync(
        int releaseId, IReadOnlyCollection<int> artifactIds, CancellationToken ct = default);

    /// <summary>Rollback runs that redeployed the release.</summary>
    Task<List<int>> GetRollbackRunIdsAsync(int releaseId, CancellationToken ct = default);

    Task<List<ReleaseArtifactInputDto>> GetArtifactInputsOfRunsAsync(
        IReadOnlyCollection<int> runIds, CancellationToken ct = default);

    /// <summary>The distinct SBOM components reported by the given runs of the project.</summary>
    Task<List<ReleasePackageInputDto>> GetPackagesOfRunsAsync(
        int projectId, IReadOnlyCollection<int> runIds, CancellationToken ct = default);

    /// <summary>The given runs that belong to the project; another project's run is left out, so its
    /// pipeline name and build number never reach this project's release page.</summary>
    Task<Dictionary<int, ReleaseRunRefDto>> GetRunRefsAsync(
        int projectId, IReadOnlyCollection<int> runIds, CancellationToken ct = default);
}
