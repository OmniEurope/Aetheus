// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Artifacts;

public interface IArtifactRepository
{
    Task<PipelineArtifact?> FindAsync(int id, CancellationToken ct = default);
    Task<(List<PipelineArtifact> Items, int TotalCount)> GetByProjectPagedAsync(int projectId, ArtifactRetentionPolicy? policy, int? pipelineId, int page, int pageSize, CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null, string? sortBy = null, bool sortDescending = true);
    Task<ProjectArtifactFilterValuesDto> GetProjectFilterValuesAsync(int projectId, CancellationToken ct = default);
    Task<List<PipelineArtifact>> GetByPipelineAndProjectAsync(int pipelineId, int projectId, ArtifactRetentionPolicy policy, CancellationToken ct = default);
    Task<List<PipelineArtifact>> GetByRunAsync(int pipelineRunId, CancellationToken ct = default);
    Task<List<PipelineArtifact>> GetByEnvironmentAsync(int pipelineId, int projectId, string environmentName, CancellationToken ct = default);
    Task<List<PipelineArtifact>> GetReleasesAsync(int pipelineId, int projectId, CancellationToken ct = default);
    Task<List<PipelineArtifact>> GetExpiredAsync(DateTime cutoff, int batchSize, CancellationToken ct = default);
    Task<bool> HasActiveRetentionLeaseAsync(int artifactId, DateTime at, CancellationToken ct = default);
    /// <summary>Recette R1-73: the <c>FilePath</c> of every artifact row, so the files no row references can be told apart.</summary>
    Task<List<string>> GetAllFilePathsAsync(CancellationToken ct = default);
    /// <summary>Recette R1-73: whether a row records exactly this <c>FilePath</c>, checked again just before a file is deleted.</summary>
    Task<bool> IsFilePathReferencedAsync(string filePath, CancellationToken ct = default);
    Task<List<PipelineArtifact>> GetProjectBuildArtifactsAsync(int projectId, CancellationToken ct = default);
    Task AddAsync(PipelineArtifact artifact, CancellationToken ct = default);
    Task LinkReleaseAsync(PipelineArtifact artifact, int releaseId, CancellationToken ct = default);
    /// <summary>Deletes the artifact row by identifier, unlinking its completed Dependency-Track
    /// deliveries in the same transaction; false when another path (the other colour,
    /// a quota eviction, a run purge) had already removed it, instead of a concurrency
    /// failure.</summary>
    Task<bool> RemoveAsync(PipelineArtifact artifact, CancellationToken ct = default);
    /// <summary>The version of the newest release that links this artifact, or null when none does.</summary>
    Task<string?> FindNewestReleaseVersionAsync(int artifactId, CancellationToken ct = default);
    Task<int> GetNextBuildNumberAsync(int projectId, int pipelineId, CancellationToken ct = default);
    // Aggregate on-record size of all artifacts of a project (for the storage quota guard).
    Task<long> GetProjectTotalSizeBytesAsync(int projectId, CancellationToken ct = default);
    // S-FEAT-15: per-project retention overrides (days); each null when the project uses the global default.
    Task<(int? DefaultDays, int? LatestDays)> GetProjectRetentionOverridesAsync(int projectId, CancellationToken ct = default);

    // Cross-agent deploy resolution. Same-run: the newest artifact named <name> produced by <runId>
    // (scenario 1). Release: the artifact of an existing release selected by id / version / "latest"
    // (scenario 3). The release selector also accepts "latest-published", which ignores drafts and
    // failed releases. Rollback compatibility gates use the dedicated previous-published lookup so a
    // release of the commit currently under test can never be mistaken for V-1. All return null when
    // nothing matches, so the deploy/restore step fails honestly.
    Task<PipelineArtifact?> FindRunArtifactByNameAsync(int runId, string name, CancellationToken ct = default);
    Task<PipelineArtifact?> FindSuccessfulPipelineArtifactByCommitAsync(
        int projectId, string pipelineName, string commitHash, string name, CancellationToken ct = default);
    // The newest successful production of <name> by <pipelineName>, whatever commit it ran on. Only
    // reached by a step that explicitly asks for `artifact_source_selector: latest-successful`,
    // because ignoring the commit is exactly what provenance must never do for a deployed payload.
    // It exists for evidence about ANOTHER commit - a nightly qualification a deployment reads - and
    // the caller is then responsible for deciding whether that evidence describes it.
    Task<PipelineArtifact?> FindLatestSuccessfulPipelineArtifactAsync(
        int projectId, string pipelineName, string name, CancellationToken ct = default);
    Task<PipelineArtifact?> FindReleaseArtifactAsync(
        int projectId, string releaseSelector, string? artifactName = null, CancellationToken ct = default);
    Task<ReleaseArtifactSelection?> FindReleaseArtifactSelectionAsync(
        int projectId, string releaseSelector, string? artifactName = null, CancellationToken ct = default);
    Task<bool> HasDeployedRollbackContractReleaseAsync(int projectId, CancellationToken ct = default);
    Task<PipelineArtifact?> FindPreviousPublishedReleaseArtifactAsync(
        int projectId, string currentCommitHash, string? artifactName = null, CancellationToken ct = default);
    Task<PipelineArtifact?> FindPreviousDeployedReleaseArtifactAsync(
        int projectId, string currentCommitHash, string? artifactName = null, CancellationToken ct = default);
    Task<bool> HasPublishedRollbackContractReleaseAsync(int projectId, CancellationToken ct = default);
    Task<bool> RequiresPreviousPublishedArtifactAsync(
        int projectId, string currentCommitHash, string? artifactName, CancellationToken ct = default);
    Task<bool> RequiresPreviousDeployedArtifactAsync(
        int projectId, string currentCommitHash, string? artifactName, CancellationToken ct = default);
    // IDOR-safe deploy download: the agent's server org must equal the artifact's project org. Null
    // when the server is unknown.
    Task<int?> GetServerOrganizationIdAsync(int serverId, CancellationToken ct = default);

    // Own-reads over entities Artifacts shares with Pipelines and Releases. Obtaining them by
    // injecting those modules' services is what put Artifacts inside a module cycle, for reads that
    // change no ownership: the writes stay where they were.
    /// <summary>
    /// The run's pipeline and the project that owns it, resolved through the pipeline's
    /// exactly-one-owner triple (Project / Environment / ProjectServer) like
    /// <see cref="GetArtifactOwningProjectIdAsync"/>; ProjectId is null only when no owner can be named.
    /// </summary>
    Task<(int PipelineId, int? ProjectId)?> GetRunPipelineContextAsync(int runId, CancellationToken ct = default);

    /// <summary>
    /// The project that owns an artifact, resolved TRANSITIVELY through its pipeline's
    /// exactly-one-owner triple (Project / Environment / ProjectServer). The artifact's own
    /// <c>ProjectId</c> column is null by design for the last two, so authorizing on that column
    /// alone leaves those artifacts unguarded. Null means no owner could be resolved, which callers
    /// must treat as a refusal, never as "no restriction".
    /// </summary>
    Task<int?> GetArtifactOwningProjectIdAsync(int artifactId, CancellationToken ct = default);

    /// <summary>Project a release belongs to; null when the release does not exist.</summary>
    Task<int?> GetReleaseProjectIdAsync(int releaseId, CancellationToken ct = default);
    Task<bool> IsServerAssignedToRunAsync(int runId, int serverId, CancellationToken ct = default);
    Task<Release?> FindReleaseForRunAsync(int pipelineRunId, CancellationToken ct = default);
    Task<List<Release>> GetDeployedProjectReleasesAsync(int projectId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed record ReleaseArtifactSelection(PipelineArtifact Artifact, int ReleaseId);
