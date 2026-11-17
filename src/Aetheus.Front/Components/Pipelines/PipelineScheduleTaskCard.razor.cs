// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>A scheduled pipeline of the build planner: its frequency in words at the reader's clock, its
/// duration, its next run and its overlap state, with the menu of what can be done to it.</summary>
public partial class PipelineScheduleTaskCard
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public ScheduledTaskDto Item { get; set; } = default!;

    /// <summary>The plan's overlaps, to name the pipelines this one shares time with.</summary>
    [Parameter, EditorRequired] public SchedulePlanSummary Summary { get; set; } = default!;

    /// <summary>The reader's zone and the present, for the local clock.</summary>
    [Parameter, EditorRequired] public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    [Parameter] public DateTime NowUtc { get; set; }

    /// <summary>Whether the reader may change this pipeline's schedule; otherwise the actions are disabled and say why.</summary>
    [Parameter] public bool CanWrite { get; set; }

    [Parameter] public int WindowDays { get; set; } = 7;
    [Parameter] public EventCallback<ScheduledTaskDto> OnEdit { get; set; }
    [Parameter] public EventCallback<ScheduledTaskDto> OnToggle { get; set; }

    /// <summary>The reader chose the branch this pipeline's definition is read on (recette R-5): the page confirms it, stores it
    /// as the pipeline's source branch and reads the plan again.</summary>
    [Parameter] public EventCallback<SourceBranchChoice> OnSourceBranchChosen { get; set; }

    /// <summary>The branches the card offers: the two the planner can read, and the pipeline's own when it is another one.</summary>
    private IReadOnlyList<OmniOption<string>> SourceOptions =>
    [
        .. ScheduleDefaults.SourceBranchChoices.Concat(Item.OwnSourceBranch is { Length: > 0 } own ? [own] : [])
            .Distinct(StringComparer.Ordinal).Select(branch => new OmniOption<string>(branch, branch))
    ];

    private string SourceText => PipelineScheduleSourceText.Label(L, Item);

    private string MissingText => PipelineScheduleSourceText.Missing(L, Item);

    // Bumped once the page has answered a choice (saved, declined or failed), so that the bar is drawn again from the stored
    // branch: a declined or failed change puts the selector back on the current branch.
    private int _selectorRevision;

    private async Task ChooseSourceAsync(string? branch)
    {
        if (branch is not { Length: > 0 } || branch == Item.SourceBranch) return;
        try
        {
            await OnSourceBranchChosen.InvokeAsync(new SourceBranchChoice(Item.PipelineId, branch));
        }
        finally
        {
            _selectorRevision++;
        }
    }

    private LocalSchedule? Local => ScheduleLocalTime.ToLocal(Item, Zone, NowUtc);
    private string FrequencyText => SchedulePhrases.Frequency(L, Item, Local);
    private string UtcText => Item.Frequency == ScheduleFrequency.Custom ? string.Empty : SchedulePhrases.Utc(L, Item);
    private string DurationText => SchedulePhrases.Duration(L, Item);
    private string? DeniedHint => CanWrite ? null : L["SchedulePermissionDenied"].Value;
    private IReadOnlyList<string> Partners =>
        Summary.PartnersByPipeline.TryGetValue(Item.PipelineId, out var names) ? names : [];

    /// <summary>A switched-off task has no occurrence at all, which the calendar cannot draw: the card says so.</summary>
    private string NextText => !Item.Enabled
        ? L["ScheduleNoRunsOff"].Value
        : Item.NextRunUtc is { } next
            ? string.Format(L["ScheduleNextRun"].Value, SchedulePhrases.DateTime(next, Zone))
            : L["ScheduleNoNextRun"].Value;

    private string OverlapText => string.Format(L["ScheduleOverlapsWith"].Value, string.Join(", ", Partners));
    private string NoOverlapText => string.Format(L["ScheduleNoOverlap"].Value, WindowDays);
}

/// <summary>The branch a reader chose, in the card of a pipeline, for the planner to read its schedule on (recette R-5).</summary>
public sealed record SourceBranchChoice(int PipelineId, string Branch);
