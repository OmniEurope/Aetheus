// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The candidate's blocking tests, each traced to the step and run that produced it.</summary>
public partial class PipelineRunBlockingTests
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public IReadOnlyList<PipelineRunAssuranceVerdict.BlockingTest> Tests { get; set; } = [];

    /// <summary>Recette R1-79: the run on screen. Its log tab finds the step among the runs it triggered,
    /// so the link keeps the reader on the parent run rather than opening the child.</summary>
    [Parameter] public int? ViewingRunId { get; set; }

    /// <summary>The run on screen (or else the producing run), opened on its logs with the producing step
    /// selected.</summary>
    internal static string LogsHref(PipelineRunGateProducer.Producer producer, int? viewingRunId = null) =>
        $"/pipelines/runs/{viewingRunId ?? producer.RunId}?tab=logs&step={producer.StepId}";
}
