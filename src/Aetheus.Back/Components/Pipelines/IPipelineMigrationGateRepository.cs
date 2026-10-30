// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Stores and reads the expand/contract gate verdicts recorded on runs (recette R1-43).</summary>
public interface IPipelineMigrationGateRepository
{
    Task AddAsync(IReadOnlyList<PipelineMigrationGateVerdict> verdicts, CancellationToken ct = default);

    /// <summary>The run's verdicts, oldest first.</summary>
    Task<List<PipelineMigrationGateVerdict>> ListForRunAsync(int runId, CancellationToken ct = default);

    Task<bool> EnvironmentExistsAsync(int environmentId, CancellationToken ct = default);

    /// <summary>
    /// The latest verdict of a migrate step that ran on one of the environment's servers, in a run of a pipeline of the
    /// environment's own project (or owned by the environment); null when none did. The environment is reached through
    /// the server the step ran on, since a run records no environment, and the run's pipeline excludes the deployments
    /// of other projects that share the server.
    /// </summary>
    Task<PipelineMigrationGateVerdict?> FindLatestForEnvironmentAsync(int environmentId, CancellationToken ct = default);

    /// <summary>
    /// The version of the release the earliest successful run deployed when the gate let
    /// <paramref name="migrationId"/> run; null when no recorded deployment applied it.
    /// </summary>
    Task<string?> FindFirstAppliedReleaseVersionAsync(string migrationId, CancellationToken ct = default);
}
