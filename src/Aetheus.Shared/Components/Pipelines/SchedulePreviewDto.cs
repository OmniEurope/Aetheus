// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>Another scheduled pipeline of the organization that the proposed schedule would run at the
/// same time as. Only pipelines the caller may read are listed.</summary>
public sealed record ScheduleOverlapDto
{
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;

    /// <summary>Minutes shared with the proposed runs over the window, covered time counted once per run.</summary>
    public int OverlapMinutes { get; init; }

    /// <summary>How many of the proposed runs of the window share time with this pipeline.</summary>
    public int OverlappingRuns { get; init; }
}

/// <summary>
/// What a proposed schedule would look like for one pipeline, computed without writing anything.
/// <see cref="Proposal"/> carries the expression, the named frequency, the duration, the next run and the
/// occurrences of the window with their <see cref="ScheduleOccurrenceDto.OverlapMinutes"/>; the pipeline's
/// present schedule is left out of the comparison, the proposal replaces it.
/// </summary>
public sealed record SchedulePreviewDto
{
    public int PipelineId { get; init; }
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public ScheduledTaskDto Proposal { get; init; } = new();
    public List<ScheduleOverlapDto> Overlaps { get; init; } = [];

    /// <summary>Why the pipeline's YAML would not take this change (a key written twice, a value on
    /// several lines, a YAML that does not parse); null when saving would be accepted as far as the YAML goes.</summary>
    public string? YamlError { get; init; }
}
