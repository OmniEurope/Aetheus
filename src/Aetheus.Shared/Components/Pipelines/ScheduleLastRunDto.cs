// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Analysis;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The latest run of a scheduled pipeline, as the planner's block says it (recette R-44): which run, how it ended,
/// how long it took and, when the quality analysis judged it, the gate and the grade. Nothing more.
/// </summary>
public sealed record ScheduleLastRunDto
{
    /// <summary>The key of the run, for the link <c>pipelines/runs/{id}</c>.</summary>
    public int RunId { get; init; }

    /// <summary>The number a person reads (<c>#12</c>).</summary>
    public int BuildNumber { get; init; }

    public PipelineStatus Status { get; init; }

    public DateTime StartedAt { get; init; }

    /// <summary>Whole minutes from start to end, at least one; null while the run is not over.</summary>
    public int? DurationMinutes { get; init; }

    /// <summary>The quality gate of the run's latest analysis; null when none was run.</summary>
    public AnalysisGateStatus? GateStatus { get; init; }

    /// <summary>The grade of that analysis; null when none or not graded.</summary>
    public AnalysisGrade? Grade { get; init; }
}
