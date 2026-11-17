// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>A scheduled server action of the planner (recette R-8): its name and server, its frequency in words at the
/// reader's clock, its duration, its next run and its overlap state. Its schedule is changed on the server's page
/// (the menu opens it) or by a drop on the calendar.</summary>
public partial class ServerActionScheduleCard
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public ScheduledServerActionDto Item { get; set; } = default!;

    /// <summary>The plan's overlaps, to name what this action shares time with.</summary>
    [Parameter, EditorRequired] public SchedulePlanSummary Summary { get; set; } = default!;

    /// <summary>The reader's zone and the present, for the local clock.</summary>
    [Parameter, EditorRequired] public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    [Parameter] public DateTime NowUtc { get; set; }
    [Parameter] public int WindowDays { get; set; } = 7;

    private string Title => ServerActionPlannerTask.Title(L, Item);
    private string Link => ServerActionPlannerTask.Link(Item);

    /// <summary>Where the action runs: its server, or for an AI task owned by a project, the project (the server is
    /// chosen at each run); empty when nothing is known beyond the title.</summary>
    private string Where => Item.ServerName.Length > 0 ? Item.ServerName : Item.ProjectName ?? string.Empty;
    private ScheduledTaskDto Row => ServerActionPlannerTask.AsTask(Item, Title);
    private LocalSchedule? Local => ScheduleLocalTime.ToLocal(Row, Zone, NowUtc);
    /// <summary>The CVE watch runs once a day at a configured local time of its own zone, not at a cron time: "Every day
    /// at 04:00 (Europe/Paris)".</summary>
    private bool IsDaily => Item.DailyTime is not null;

    private string FrequencyText => Item.MachineReboot is not null
        ? MachineRebootText.When(L, Item)
        : Item.MachineJob is not null
            ? MachineJobText.When(L, Item)
            : Item.DailyTime is { } time
                ? string.Format(L["ScheduleCveDaily"].Value, time, Item.DailyTimeZoneId)
                : SchedulePhrases.Frequency(L, Row, Local);

    /// <summary>What decides a reboot the machine plans by itself (recette R-11); null for every other sort.</summary>
    private string? MachineRebootSourceText => Item.MachineReboot is { } entry ? MachineRebootText.Source(L, entry) : null;

    /// <summary>What schedules a job the machine runs by itself (recette R-27); null for every other sort.</summary>
    private string? MachineJobSourceText => Item.MachineJob is { } job ? MachineJobText.Source(L, job) : null;

    /// <summary>Whether the action counts in the overlaps, hence whether "no overlap" says something about it.</summary>
    private bool CountsInOverlaps => ServerActionDefaults.CountsInOverlaps(Item.Kind);
    private string UtcText => Item.Frequency == ScheduleFrequency.Custom ? string.Empty : SchedulePhrases.Utc(L, Row);
    private string DurationText => SchedulePhrases.Duration(L, Row);

    private IReadOnlyList<string> Partners =>
        Summary.PartnersByPipeline.TryGetValue(ServerActionPlannerTask.RowId(Item), out var names) ? names : [];

    private string NextText => Item.NextRunUtc is { } next
        ? string.Format(L["ScheduleNextRun"].Value, SchedulePhrases.DateTime(next, Zone))
        : L["ScheduleNoNextRun"].Value;

    private string OverlapText => string.Format(L["ScheduleOverlapsWith"].Value, string.Join(", ", Partners));
    private string NoOverlapText => string.Format(L["ScheduleNoOverlap"].Value, WindowDays);
}
