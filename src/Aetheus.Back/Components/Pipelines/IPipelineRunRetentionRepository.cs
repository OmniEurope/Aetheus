// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The pipeline-run retention: deletes the runs older than a cutoff that no live release or pending
/// deferred cleanup still needs. The port of the retention service (audit AUD-07).
/// <see cref="IPipelineRepository"/> composes it and delegates to the same query.
/// </summary>
public interface IPipelineRunRetentionRepository
{
    Task<int> DeleteRunsOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
