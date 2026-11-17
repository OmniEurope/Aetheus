// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The build planner's calendar (PLAN-005 lot 4): the night (20:00 to 08:00) or the week of the plan, one block
/// per occurrence over its duration, a drop reported as a <see cref="ScheduleMoveWrite"/> for the page to write.
/// The scheduler loads what it shows: the plan the page read when the range lies inside its window, otherwise
/// the plan of that range (<see cref="LoadRange"/>), so moving to another night or week needs no window of the
/// page's own. A new plan from the page (after every write) makes the scheduler load again. A planned
/// duration, an overlap and a block that cannot move are said in the title of the block and drawn with a pattern.
/// </summary>
public partial class PipelineSchedulePlanView
{
    internal const string Night = SchedulePlanViewNames.Night;
    internal const string Day = SchedulePlanViewNames.Day;
    internal const string Week = SchedulePlanViewNames.Week;
    internal const string Cards = SchedulePlanViewNames.Cards;

    private static readonly TimeOnly NightStart = new(20, 0);
    private static readonly TimeOnly NightEnd = new(8, 0);

    // The day view (R-38) is the 24 hours: OmniScheduler draws no grid when the end equals the start, so the last
    // slot ends at 23:59 and the grid holds 96 quarter hours.
    private static readonly TimeOnly DayStart = new(0, 0);
    private static readonly TimeOnly DayEnd = new(23, 59);
    private static readonly TimeSpan Slot = TimeSpan.FromMinutes(15);

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    /// <summary>The plan the page read: its tasks, and the window it covers.</summary>
    [Parameter, EditorRequired] public SchedulePlanDto Plan { get; set; } = default!;

    /// <summary>The reader's zone: the blocks are drawn in it and a drop is read in it.</summary>
    [Parameter, EditorRequired] public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    /// <summary>The present, to open on the night in progress or the coming one.</summary>
    [Parameter] public DateTime NowUtc { get; set; }

    /// <summary>Whether the reader may change a task's schedule.</summary>
    [Parameter] public Func<ScheduledTaskDto, bool>? CanWrite { get; set; }

    /// <summary>The plan of a range the page's own plan does not cover (UTC, start included, end excluded); null
    /// when it cannot be read, the page then says why.</summary>
    [Parameter] public Func<DateTime, DateTime, CancellationToken, Task<SchedulePlanDto?>>? LoadRange { get; set; }

    /// <summary>Raised once per drop that changes a schedule; the page writes it.</summary>
    [Parameter] public EventCallback<ScheduleMoveWrite> OnMove { get; set; }

    /// <summary>Raised once per drop that changes the schedule of a server action (R-8); the page writes it.</summary>
    [Parameter] public EventCallback<ServerActionMoveWrite> OnServerActionMove { get; set; }

    [Inject] private NavigationManager Nav { get; set; } = default!;

    /// <summary>R-46: the view in the URL (<c>?view=</c>); the browser's back button restores the previous one.</summary>
    [Parameter, SupplyParameterFromQuery(Name = PipelineSchedulePlanUrl.ViewKey)] public string? ViewQuery { get; set; }

    /// <summary>R-46: the day in the URL (<c>?date=</c>, an ISO day).</summary>
    [Parameter, SupplyParameterFromQuery(Name = PipelineSchedulePlanUrl.DateKey)] public string? DateQuery { get; set; }

    // R-3: the direction of the time axis, kept for the life of the page only (not in the URL); OE's default is vertical.
    private OmniOrientation _orientation = OmniOrientation.Vertical;
    private string _mode = Night;
    private string _defaultMode = Night;
    private DateTimeOffset _defaultNightDate;
    private DateTimeOffset _defaultWeekDate;
    private DateTimeOffset _nightDate;
    private DateTimeOffset _dayDate;
    private DateTimeOffset _weekDate;
    private SchedulePlanDto? _loadedFor;
    private Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<OmniSchedulerAppointment>>> _load = default!;
    private IReadOnlyList<OmniSchedulerAppointment> _items = [];

    // Null while the page's plan holds the span shown (the scheduler then draws _items); otherwise the loader of that span.
    private Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<OmniSchedulerAppointment>>>? _loadParameter;

    private int SwitchedOff => Plan.Tasks.Count(task => !task.Enabled);

    private IReadOnlyList<OmniOption<string>> ModeOptions =>
    [
        new(Night, L["ScheduleViewNight"].Value),
        new(Day, L["ScheduleViewDay"].Value),
        new(Week, L["ScheduleViewWeek"].Value),
        new(Cards, L["ScheduleViewCards"].Value)
    ];

    private IReadOnlyList<OmniOption<OmniOrientation>> OrientationOptions =>
    [
        new(OmniOrientation.Vertical, L["ScheduleOrientationVertical"].Value),
        new(OmniOrientation.Horizontal, L["ScheduleOrientationHorizontal"].Value)
    ];

    private static OmniIconName OrientationIcon(OmniOrientation orientation) =>
        orientation == OmniOrientation.Horizontal ? OmniIconName.Rows : OmniIconName.Columns;

    private void OnOrientationChanged(OmniOrientation orientation) => _orientation = orientation;

    private static OmniIconName ModeIcon(string mode) => mode switch
    {
        Night => OmniIconName.ThemeDark,
        Day => OmniIconName.ThemeLight,
        Week => OmniIconName.CalendarBlank,
        _ => OmniIconName.Cards
    };

    protected override async Task OnInitializedAsync()
    {
        _defaultNightDate = InZone(SchedulePlanSummary.Night(Zone, NowUtc).StartUtc);
        _defaultWeekDate = InZone(NowUtc);
        // Under 40rem the cards are the default view: the calendar is wider than a phone.
        if (await Js.InvokeAsync<bool>("Aetheus.isNarrowViewport")) _defaultMode = Cards;
    }

    protected override void OnParametersSet()
    {
        // The URL is the source of truth (R-46): without a value, the planner's own default.
        _mode = PipelineSchedulePlanUrl.ParseView(ViewQuery) ?? _defaultMode;
        if (PipelineSchedulePlanUrl.ParseDate(DateQuery) is { } day)
        {
            _nightDate = _dayDate = _weekDate = PipelineSchedulePlanUrl.AtMidday(day, Zone);
        }
        else
        {
            _nightDate = _defaultNightDate;
            _dayDate = _weekDate = _defaultWeekDate;
        }

        if (!ReferenceEquals(_loadedFor, Plan))
        {
            // A new closure each time the plan changes: the scheduler loads again when its loader is another one.
            var plan = Plan;
            _loadedFor = plan;
            _load = (start, end, ct) => LoadAppointmentsAsync(plan, start, end, ct);
            _items = Appointments(plan);
        }

        // R-43: the plan the page read draws the span shown when it holds it, so a new plan after a write is a redraw
        // of the blocks and not a loading state that rebuilds the whole grid after a second read.
        _loadParameter = Covers() ? null : _load;
    }

    private bool Covers()
    {
        if (_mode == Cards) return true;
        var date = _mode switch { Night => _nightDate, Day => _dayDate, _ => _weekDate };
        var span = SchedulePlanWindow.Visible(_mode, PipelineSchedulePlanUrl.DayOf(date, Zone), Zone, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek, NightEnd);
        return SchedulePlanWindow.Covers(Plan, span);
    }

    /// <summary>A pick of the view bar pushes a history entry; the day already in the URL is kept.</summary>
    private void OnModeChanged(string mode)
    {
        _mode = mode;
        GoTo(mode, PipelineSchedulePlanUrl.ParseDate(DateQuery));
    }

    /// <summary>The scheduler moved to another day (arrows, Today): pushed as a history entry so back returns to the
    /// previous day. The same day as the URL's pushes nothing.</summary>
    private void OnDateChanged(DateTimeOffset date)
    {
        var day = PipelineSchedulePlanUrl.DayOf(date, Zone);
        if (PipelineSchedulePlanUrl.ParseDate(DateQuery) == day) return;
        GoTo(_mode, day);
    }

    private void GoTo(string mode, DateOnly? day) =>
        Nav.NavigateTo(Nav.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            [PipelineSchedulePlanUrl.ViewKey] = mode,
            [PipelineSchedulePlanUrl.DateKey] = day is { } d ? PipelineSchedulePlanUrl.FormatDate(d) : null
        }));

    private async Task<IReadOnlyList<OmniSchedulerAppointment>> LoadAppointmentsAsync(
        SchedulePlanDto plan, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var from = start.UtcDateTime;
        var to = end.UtcDateTime;
        var covered = plan.FromUtc <= from && to <= plan.ToUtc;
        var source = covered || LoadRange is null ? plan : await LoadRange(from, to, ct);
        return source is null ? [] : Appointments(source);
    }

    /// <summary>One appointment per occurrence of a switched-on task, then of each server action (R-8). A
    /// switched-off task has no occurrence, so nothing is drawn for it.</summary>
    private IReadOnlyList<OmniSchedulerAppointment> Appointments(SchedulePlanDto plan) =>
    [
        .. plan.Tasks.Where(task => task.Enabled).SelectMany(task => task.Occurrences.Select(occurrence =>
            new OmniSchedulerAppointment(
                AppointmentId(task.PipelineId, occurrence.StartUtc), Title(task, occurrence),
                InZone(occurrence.StartUtc), InZone(occurrence.EndUtc), SchedulePhrases.Duration(L, task))
            {
                Class = BlockClass(task, occurrence)
            })),
        .. ServerActionBlocks.Appointments(plan, L, InZone)
    ];

    /// <summary>The identity of the block of one occurrence: the pipeline, then its start in UTC.</summary>
    internal static string AppointmentId(int pipelineId, DateTime startUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{pipelineId}|{startUtc:O}");

    private bool Movable(ScheduledTaskDto task) =>
        task.Frequency != ScheduleFrequency.Custom && task.Hour is not null && task.Minute is not null && (CanWrite?.Invoke(task) ?? false);

    /// <summary>The block's name and what marks it apart, in words (the pattern of its class is never the only
    /// sign): planned duration, minutes shared with another run, cannot be moved.</summary>
    private string Title(ScheduledTaskDto task, ScheduleOccurrenceDto occurrence)
    {
        var marks = new List<string>();
        if (task.IsPlanned) marks.Add(L["ScheduleBlockPlanned"].Value);
        // R-5: the branch the definition, hence this time, was read on.
        if (task.CanChooseSourceBranch && task.SourceBranch is { Length: > 0 } branch)
            marks.Add(string.Format(L["ScheduleBlockBranch"].Value, branch));
        if (occurrence.OverlapMinutes > 0)
            marks.Add(string.Format(L[Plan.ExecutionCapacity > 1 ? "ScheduleBlockOverCapacity" : "ScheduleBlockOverlap"].Value, SchedulePhrases.Minutes(occurrence.OverlapMinutes)));
        if (!Movable(task)) marks.Add(L["ScheduleBlockLocked"].Value);
        return marks.Count == 0 ? task.PipelineName : $"{task.PipelineName} ({string.Join(", ", marks)})";
    }

    private string BlockClass(ScheduledTaskDto task, ScheduleOccurrenceDto occurrence) =>
        string.Join(' ', new[]
        {
            "schedule-block",
            task.IsPlanned ? "schedule-block--planned" : null,
            occurrence.OverlapMinutes > 0 ? "schedule-block--overlap" : null,
            Movable(task) ? null : "schedule-block--locked"
        }.Where(name => name is not null));

    private DateTimeOffset InZone(DateTime utc) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), Zone);

    /// <summary>A drop on the calendar: decided by <see cref="ScheduleMovePlanner"/>, a refusal is said in a toast
    /// and writes nothing, a drop that changes nothing writes nothing, anything else is handed to the page once.</summary>
    private async Task MoveAsync(OmniSchedulerAppointmentMove move)
    {
        if (ServerActionBlocks.Find(Plan, move.Appointment.Id) is { } action)
        {
            await MoveServerActionAsync(move, action);
            return;
        }

        if (TaskOf(move.Appointment) is not { } task) return;
        var decision = ScheduleMovePlanner.Decide(task, move.Appointment.Start, move.Start, Zone, CanWrite?.Invoke(task) ?? false);
        if (decision.Refusal is { } refusal)
        {
            Notify.Warning("ScheduleMoveRefusedTitle", refusal);
            return;
        }

        if (decision.Spec is not { } spec)
        {
            if (decision.DateIgnored) Notify.Info("ScheduleMoveUnchangedTitle", "ScheduleMoveDateIgnored", task.PipelineName);
            return;
        }

        var words = SchedulePhrases.Frequency(L, task.Frequency, decision.LocalDay, decision.LocalTime);
        var detail = string.Format(L[decision.CommonTime ? "ScheduleMovedCommonTime" : "ScheduleSavedDetail"].Value, task.PipelineName, words);
        await OnMove.InvokeAsync(new ScheduleMoveWrite(task, ScheduleMovePlanner.RequestOf(task, spec), detail, ScheduleMovePlanner.Previous(task)));
    }

    /// <summary>A drop of a scheduled action's block (R-8): the same rules as a pipeline's (<see cref="ScheduleMovePlanner"/>),
    /// the right being the one the plan read for that action (<see cref="ScheduledServerActionDto.Movable"/>). The page writes
    /// the schedule where the action keeps it.</summary>
    private async Task MoveServerActionAsync(OmniSchedulerAppointmentMove move, ScheduledServerActionDto action)
    {
        var title = ServerActionPlannerTask.Title(L, action);
        if (action.Kind == ServerActionKind.CveWatch)
        {
            await MoveCveWatchAsync(move, action, title);
            return;
        }

        var task = ServerActionPlannerTask.AsTask(action, title);
        var decision = ScheduleMovePlanner.Decide(task, move.Appointment.Start, move.Start, Zone, action.Movable);
        if (decision.Refusal is { } refusal)
        {
            // The pipeline sentence of a custom schedule talks of a pipeline's file: an action has its own. A sort that is read
            // only by design says why, not that the reader lacks a right.
            var readOnlyBySort = refusal == "SchedulePermissionDenied" && !ServerActionDefaults.CanBeMoved(action.Kind);
            Notify.Warning("ScheduleMoveRefusedTitle",
                refusal == "ScheduleMoveCustom" ? ServerActionPlannerTask.MoveCustomKey(action.Kind)
                : readOnlyBySort ? ServerActionPlannerTask.ReadOnlyKey(action.Kind)
                : refusal);
            return;
        }

        if (decision.Spec is not { } spec || ScheduleFrequencyCodec.Encode(spec) is not { } engineCron)
        {
            if (decision.DateIgnored) Notify.Info("ScheduleMoveUnchangedTitle", "ScheduleMoveDateIgnored", title);
            return;
        }

        var words = SchedulePhrases.Frequency(L, task.Frequency, decision.LocalDay, decision.LocalTime);
        var detail = string.Format(L[decision.CommonTime ? "ScheduleMovedCommonTime" : "ScheduleSavedDetail"].Value, title, words);
        await OnServerActionMove.InvokeAsync(new ServerActionMoveWrite(action, ServerActionDefaults.ToStoredFields(action.Kind, engineCron), detail, action.CronExpression));
    }

    /// <summary>A drop of the CVE watch block: the watch has one local time a day, so the time of the drop (read in the zone of the
    /// watch, <see cref="CveWatchMovePlanner"/>) is the new time of its daily pass and its date is ignored. Only a site
    /// administrator gets a movable block, and the endpoint that stores it refuses any other role again.</summary>
    private async Task MoveCveWatchAsync(OmniSchedulerAppointmentMove move, ScheduledServerActionDto action, string title)
    {
        var decision = CveWatchMovePlanner.Decide(action, move.Appointment.Start, move.Start, Zone);
        if (decision.Refusal is not null)
        {
            Notify.Warning("ScheduleMoveRefusedTitle", ServerActionPlannerTask.ReadOnlyKey(action.Kind));
            return;
        }

        if (decision.PassTime is not { } passTime)
        {
            if (decision.DateIgnored) Notify.Info("ScheduleMoveUnchangedTitle", "ScheduleMoveDateIgnored", title);
            return;
        }

        var words = string.Format(L["ScheduleCveDaily"].Value, passTime, action.DailyTimeZoneId);
        var detail = string.Format(L["ScheduleMovedCommonTime"].Value, title, words);
        await OnServerActionMove.InvokeAsync(new ServerActionMoveWrite(action, passTime, detail, action.DailyTime!));
    }

    /// <summary>R-44: a click on a block opens the small info of its pipeline (<see cref="PipelineScheduleBlockInfo"/>), or of its
    /// server action (<see cref="ServerActionBlockInfo"/>, R-8).</summary>
    private async Task OpenInfoAsync(OmniSchedulerAppointment appointment)
    {
        if (ServerActionBlocks.Find(Plan, appointment.Id) is { } action)
        {
            await Dialog.OpenAsync<ServerActionBlockInfo>(
                ServerActionPlannerTask.Title(L, action),
                new Dictionary<string, object?> { ["Action"] = action },
                new OmniDialogOptions { Width = "440px", AutoFocusFirstElement = false });
            return;
        }

        if (TaskOf(appointment) is not { } task) return;
        await Dialog.OpenAsync<PipelineScheduleBlockInfo>(
            task.PipelineName,
            new Dictionary<string, object?> { ["Task"] = task },
            new OmniDialogOptions { Width = "440px", AutoFocusFirstElement = false });
    }

    private ScheduledTaskDto? TaskOf(OmniSchedulerAppointment appointment)
    {
        var separator = appointment.Id.IndexOf('|', StringComparison.Ordinal);
        return separator > 0 && int.TryParse(appointment.Id.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var pipelineId)
            ? Plan.Tasks.FirstOrDefault(task => task.PipelineId == pipelineId)
            : null;
    }
}
