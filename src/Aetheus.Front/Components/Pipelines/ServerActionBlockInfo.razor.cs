// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The small info of a block of a server action in the planner's calendar (recette R-8): its server, its
/// duration, where its schedule comes from and whether the planner may move it.</summary>
public partial class ServerActionBlockInfo
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    /// <summary>The scheduled server action the block belongs to.</summary>
    [Parameter, EditorRequired] public ScheduledServerActionDto Action { get; set; } = default!;

    private string? ServerHref => ServerActionPlannerTask.ServerLink(Action);

    private string? ProjectHref => ServerActionPlannerTask.ProjectLink(Action);

    private string DurationText => SchedulePhrases.Duration(L, ServerActionPlannerTask.AsTask(Action, string.Empty));
}
