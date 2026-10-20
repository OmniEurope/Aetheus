// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Analysis;

public interface IAnalysisRunResultService
{
    /// <summary>The analysis result of a run as its page shows it. A finished run's result is computed
    /// once per stamp (see <see cref="AnalysisRunResultCache"/>).</summary>
    Task<AnalysisRunGateDto> GetRunResultAsync(int runId, CancellationToken ct = default);

    /// <summary>One page of the findings a set of runs observed, with the set's counts.</summary>
    Task<AnalysisRunFindingsPageDto> GetRunFindingsAsync(AnalysisRunFindingsRequest request, CancellationToken ct = default);
}

/// <summary> <c>GET api/Analysis/runs/{runId}/result</c> and its findings pages. The run is read as
/// its stamp (one statement of indexed lookups) instead of its context, which carried the pipeline
/// YAML; a finished run's result is reused while its stamp holds; Dependency-Track is applied live.
/// </summary>
public sealed class AnalysisRunResultService(
    IAnalysisRepository repository,
    IAnalysisRunResultRepository results,
    AnalysisRunResultCache cache,
    IOptions<DependencyTrackOptions> dependencyTrackOptions) : IAnalysisRunResultService
{
    public async Task<AnalysisRunGateDto> GetRunResultAsync(int runId, CancellationToken ct = default)
    {
        var stamp = await results.GetResultStampAsync(runId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Pipeline run not found or not owned by a project.");

        if (!stamp.Finished || !cache.TryGet(runId, stamp, out var gate))
        {
            gate = await repository.GetRunResultSummaryAsync(runId, AnalysisRunGateDto.SummaryFindingLimit, ct).ConfigureAwait(false);
            if (stamp.Finished)
                cache.Set(runId, stamp, gate);
        }
        return await DependencyTrackGateVerdict.ApplyAsync(
            gate, runId, null, dependencyTrackOptions.Value, repository, ct).ConfigureAwait(false);
    }

    public Task<AnalysisRunFindingsPageDto> GetRunFindingsAsync(AnalysisRunFindingsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (page, pageSize) = request.Normalize();
        var runIds = request.RunIds.Distinct().Take(AnalysisRunFindingsRequest.MaxRuns).ToList();
        return results.GetFindingsPageAsync(runIds, request, page, pageSize, ct);
    }
}
