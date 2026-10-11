// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The candidate's blocking tests, each traced to the step and run that produced it.</summary>
public partial class PipelineRunBlockingTests
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public IReadOnlyList<PipelineRunAssuranceVerdict.BlockingTest> Tests { get; set; } = [];

    /// <summary>The producing run, opened on its logs with the producing step selected.</summary>
    internal static string LogsHref(PipelineRunGateProducer.Producer producer) =>
        $"/pipelines/runs/{producer.RunId}?tab=logs&step={producer.StepId}";
}
