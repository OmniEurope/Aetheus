// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Stores and reads the expand/contract gate verdicts recorded on runs (recette R1-43).</summary>
public interface IPipelineMigrationGateRepository
{
    Task AddAsync(IReadOnlyList<PipelineMigrationGateVerdict> verdicts, CancellationToken ct = default);

    /// <summary>The run's verdicts, oldest first.</summary>
    Task<List<PipelineMigrationGateVerdict>> ListForRunAsync(int runId, CancellationToken ct = default);
}
