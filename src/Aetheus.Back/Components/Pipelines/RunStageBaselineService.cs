// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Provides historical stage and step duration baselines for a pipeline run.</summary>
public interface IRunStageBaselineService
{
    Task<RunStageBaselinesDto> GetAsync(int pipelineId, int runId, CancellationToken ct = default);
}

/// <summary>
/// The query decides which runs count (successful, same pipeline, started before the run, at most
/// <see cref="SampleSize"/>); <see cref="RunStageBaselineCalculator"/> does the arithmetic.
/// </summary>
public sealed class RunStageBaselineService(IPipelineRepository pipelines) : IRunStageBaselineService
{
    /// <summary>Runs the average is drawn from: enough to smooth one slow run out, few enough that a
    /// change of habit (a faster runner, a heavier test suite) shows within a day of runs.</summary>
    public const int SampleSize = 10;

    public async Task<RunStageBaselinesDto> GetAsync(int pipelineId, int runId, CancellationToken ct = default)
    {
        var rows = await pipelines
            .GetRecentSuccessfulStepTimingsAsync(pipelineId, runId, SampleSize, ct)
            .ConfigureAwait(false);
        return RunStageBaselineCalculator.Compute(rows);
    }
}
