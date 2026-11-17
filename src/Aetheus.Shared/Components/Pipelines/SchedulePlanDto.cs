// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>One occurrence of a scheduled task inside the requested UTC window. The interval is
/// half-open: <c>[StartUtc, EndUtc)</c>, so an end equal to the next start is not an overlap.</summary>
public sealed record ScheduleOccurrenceDto
{
    public DateTime StartUtc { get; init; }
    public DateTime EndUtc { get; init; }

    /// <summary>Minutes shared with another task of the same organisation; 0 when none.</summary>
    public int OverlapMinutes { get; init; }
}

public sealed record ScheduledTaskDto
{
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;

    /// <summary>True when <c>trigger: schedule</c>; false when the schedule value is kept but the
    /// pipeline is switched to manual.</summary>
    public bool Enabled { get; init; }

    /// <summary>The cron expression as written in the YAML (six fields, seconds first, UTC).</summary>
    public string CronExpression { get; init; } = string.Empty;

    public ScheduleFrequency Frequency { get; init; } = ScheduleFrequency.Custom;

    /// <summary>Day of week (0 = Sunday) for <see cref="ScheduleFrequency.Weekly"/>, day of month
    /// (1 to 28) for <see cref="ScheduleFrequency.Monthly"/>, otherwise null.</summary>
    public int? Day { get; init; }

    /// <summary>UTC hour and minute; null for a custom expression that is not a single time.</summary>
    public int? Hour { get; init; }
    public int? Minute { get; init; }

    /// <summary>Average of the last successful runs, or <see cref="ScheduleDefaults.PlannedMinutes"/>
    /// when there is none.</summary>
    public int DurationMinutes { get; init; } = ScheduleDefaults.PlannedMinutes;

    /// <summary>True when <see cref="DurationMinutes"/> is the planned default (never ran).</summary>
    public bool IsPlanned { get; init; } = true;

    public DateTime? NextRunUtc { get; init; }
    public List<ScheduleOccurrenceDto> Occurrences { get; init; } = [];

    /// <summary>True when the task fires more often in the window than <see cref="Occurrences"/> can
    /// hold: the time after its last occurrence is not free, whatever the list shows.</summary>
    public bool OccurrencesTruncated { get; init; }

    /// <summary>The pipeline's latest run, for the small info of its block (recette R-44); null when it never ran.</summary>
    public ScheduleLastRunDto? LastRun { get; init; }

    /// <summary>The branch of the project's repository whose definition this task's schedule was read from (recette R-5):
    /// the pipeline's own source branch, or the branch the reader chose in the planner (see
    /// <see cref="ScheduleDefaults.SourceBranchChoices"/>). Null: the repository's default branch.</summary>
    public string? SourceBranch { get; init; }

    /// <summary>The source branch set on the pipeline itself (null: the repository's default branch). It differs from
    /// <see cref="SourceBranch"/> when the reader chose another branch: that choice only changes what the planner shows.</summary>
    public string? OwnSourceBranch { get; init; }

    /// <summary>True when the pipeline's definition lives in a project's repository, so that the planner can read it on another
    /// branch; false for a pipeline of no project.</summary>
    public bool CanChooseSourceBranch { get; init; }

    /// <summary>True when the reader chose a branch (<see cref="SourceBranch"/>) on which no readable definition of this
    /// pipeline exists: the schedule shown is then the pipeline's own (<see cref="OwnSourceBranch"/>), and the card says so.</summary>
    public bool ChosenBranchMissing { get; init; }
}

public sealed record SchedulePlanDto
{
    public int OrganizationId { get; init; }
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }

    /// <summary>How many runs the organization can execute at once (recette R-39): its pipeline runners, at least
    /// one. Runs side by side within it are not an overlap; <see cref="ScheduleOccurrenceDto.OverlapMinutes"/>
    /// counts only the time beyond it.</summary>
    public int ExecutionCapacity { get; init; } = 1;

    public List<ScheduledTaskDto> Tasks { get; init; } = [];

    /// <summary>The scheduled actions of the organization's servers the reader may read (recette R-8), drawn beside
    /// the pipelines; their overlap minutes count the pipelines too.</summary>
    public List<ScheduledServerActionDto> ServerActions { get; init; } = [];
}
