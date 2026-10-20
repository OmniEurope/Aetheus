// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// View model for one pipeline stage on the run view (a stage groups its step runs). Promoted from a
/// private nested type to a top-level type so <see cref="PipelineRunTimelineBuilder"/> and the
/// <c>PipelineRun</c> code-behind can share it without a <c>partial</c> split.
/// </summary>
internal sealed class StageViewModel
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public List<PipelineStepRunDto> Steps { get; init; } = [];
    public TaskExecutionStatus Status { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public bool IsSystem { get; init; }
    public string? GroupName { get; init; }

    /// <summary>Depth of this stage in the run's `depends_on` graph. Stages sharing a depth ran
    /// concurrently; null when the run carries no usable definition snapshot.</summary>
    public int? Depth { get; init; }

    /// <summary>Explains the unmet condition when every step in a stage was cancelled by that
    /// condition.</summary>
    public string? SkippedCondition { get; init; }

    /// <summary>Recette R1-80: the stage completed and one of its controls ran and found failures.</summary>
    public bool IsRejected { get; init; }
}
