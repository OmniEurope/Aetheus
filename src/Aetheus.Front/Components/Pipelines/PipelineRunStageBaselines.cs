// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Stage and step baselines use successful-run averages returned by the server, keyed by
/// run ID. Each run's fixed historical baseline needs only one request.</summary>
internal sealed class PipelineRunStageBaselines(ApiClient api)
{
    private readonly Dictionary<int, RunStageBaselinesDto> _byRunId = [];
    private readonly HashSet<int> _resolvedRunIds = [];

    public IReadOnlyDictionary<int, RunStageBaselinesDto> Runs => _byRunId;

    public async Task LoadAsync(IEnumerable<PipelineRunDto> runs)
    {
        var pending = runs
            .Where(run => !_resolvedRunIds.Contains(run.Id))
            .DistinctBy(run => run.Id)
            .ToList();
        if (pending.Count == 0) return;

        var results = await Task.WhenAll(pending.Select(LoadOneAsync));
        foreach (var (runId, baselines, succeeded) in results)
        {
            // A failed request is retried on the next reload rather than remembered as "no history".
            if (!succeeded) continue;
            _resolvedRunIds.Add(runId);
            if (baselines is { SampleRuns: > 0 })
                _byRunId[runId] = baselines;
        }
    }

    private async Task<(int RunId, RunStageBaselinesDto? Baselines, bool Succeeded)> LoadOneAsync(PipelineRunDto run)
    {
        try
        {
            return (run.Id, await api.Pipelines.GetStageBaselinesAsync(run.PipelineId, run.Id), true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return (run.Id, null, false);
        }
    }
}
