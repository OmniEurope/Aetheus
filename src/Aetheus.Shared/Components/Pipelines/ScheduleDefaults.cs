// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

public static class ScheduleDefaults
{
    /// <summary>Duration assumed for a scheduled pipeline that has never completed a successful run.
    /// The task then carries <see cref="ScheduledTaskDto.IsPlanned"/>.</summary>
    public const int PlannedMinutes = 60;

    /// <summary>Longest window the schedule plan accepts, in days: one month of the calendar.</summary>
    public const int MaxWindowDays = 31;

    /// <summary>The branches the planner can read a pipeline's definition on (recette R-5): the selector of a card and the
    /// <c>sourceBranch</c> query of the plan endpoint accept these two. It only changes what the planner shows.</summary>
    public static readonly IReadOnlyList<string> SourceBranchChoices = ["main", "develop"];
}
