// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Tasks;

/// <summary>ADR-060, step 2: fills <c>Uid</c> and <c>Number</c> on the tasks written before the version
/// that sets them, or by the previous version during a blue-green switch.</summary>
public interface ITaskIdentityBackfillRepository
{
    /// <summary>Fills the oldest rows after <paramref name="afterId"/> and returns the highest id handled,
    /// or null when nothing is left past it.</summary>
    Task<int?> FillNextBatchAsync(int afterId, int batchSize, CancellationToken ct = default);
}
