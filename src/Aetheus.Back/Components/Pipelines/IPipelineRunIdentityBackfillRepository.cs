// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>ADR-060, step 2: fills <c>Uid</c> on the runs written before the version that sets it, or by
/// the previous version during a blue-green switch.</summary>
public interface IPipelineRunIdentityBackfillRepository
{
    /// <summary>Fills the oldest rows after <paramref name="afterId"/> and returns the highest id handled,
    /// or null when nothing is left past it.</summary>
    Task<int?> FillNextBatchAsync(int afterId, int batchSize, CancellationToken ct = default);

    /// <summary>All the runs, and those still without a public identifier.</summary>
    Task<PipelineRunIdentityBackfillDto> GetProgressAsync(CancellationToken ct = default);
}
