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
}
