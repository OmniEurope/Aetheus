// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What the planner says of the branch a pipeline's schedule was read on (recette R-5), in the card and in the small
/// info of a block, so that both say the same thing.</summary>
internal static class PipelineScheduleSourceText
{
    /// <summary>"Definition read on branch main", or the repository's default branch; when the reader chose another branch than
    /// the pipeline's own, that it is a view and which branch the pipeline itself reads.</summary>
    public static string Label(IStringLocalizer<AppStrings> localizer, ScheduledTaskDto task) =>
        task.SourceBranch is { Length: > 0 } branch
            ? string.Format(
                localizer[branch == task.OwnSourceBranch ? "ScheduleSourceBranch" : "ScheduleSourceViewed"].Value,
                branch, OwnBranchName(localizer, task))
            : localizer["ScheduleSourceDefault"].Value;

    /// <summary>The chosen branch has no readable definition: the pipeline's own schedule is the one shown.</summary>
    public static string Missing(IStringLocalizer<AppStrings> localizer, ScheduledTaskDto task) =>
        string.Format(localizer["ScheduleSourceMissing"].Value, task.SourceBranch, OwnBranchName(localizer, task));

    /// <summary>The name of a branch for a sentence: the branch itself, or "the default branch" when none is set.</summary>
    public static string BranchName(IStringLocalizer<AppStrings> localizer, string? branch) =>
        branch is { Length: > 0 } ? branch : localizer["ScheduleSourceDefaultName"].Value;

    private static string OwnBranchName(IStringLocalizer<AppStrings> localizer, ScheduledTaskDto task) =>
        task.OwnSourceBranch is { Length: > 0 } own ? own : localizer["ScheduleSourceDefaultName"].Value;
}
