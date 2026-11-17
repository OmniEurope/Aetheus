// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The body of <c>PUT api/pipelines/{id}/schedule</c>: moves, enables or disables the schedule of a
/// pipeline. <see cref="Frequency"/> with <see cref="Day"/>, <see cref="Hour"/> and <see cref="Minute"/>
/// (UTC) sets a new schedule; a null <see cref="Frequency"/> keeps the value in the YAML and only applies
/// <see cref="Enabled"/>. The two <c>Expected</c> fields are what the caller last read, from
/// <see cref="ScheduledTaskDto.CronExpression"/> and <see cref="ScheduledTaskDto.Enabled"/>: when the
/// pipeline no longer says that, the write is refused (409).
/// </summary>
public sealed record UpdatePipelineScheduleRequest
{
    /// <summary>The frequency to write; null leaves the schedule as written. <see cref="ScheduleFrequency.Custom"/>
    /// cannot be written and is refused.</summary>
    public ScheduleFrequency? Frequency { get; init; }

    /// <summary>The day of the week (0 to 6, Sunday first) of a weekly schedule, or of the month (1 to 28)
    /// of a monthly one.</summary>
    public int? Day { get; init; }

    /// <summary>The hour of the run, in UTC.</summary>
    public int? Hour { get; init; }

    /// <summary>The minute of the run, in UTC.</summary>
    public int? Minute { get; init; }

    /// <summary>True: the engine fires the schedule. False: the value stays in the YAML and nothing fires.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The expression the caller read; null or empty when the pipeline had none.</summary>
    [StringLength(255)]
    public string? ExpectedCronExpression { get; init; }

    /// <summary>Whether the caller read the schedule as enabled.</summary>
    public bool ExpectedEnabled { get; init; }
}
