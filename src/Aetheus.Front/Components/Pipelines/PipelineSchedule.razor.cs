// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The build planner (PLAN-005 lot 3, recette R-21): the active organization's scheduled pipelines over the next
/// <see cref="WindowDays"/> days, as tiles, a night and week calendar and cards. Times are the reader's local ones, the UTC time the engine
/// reads is secondary. The page follows the active organization and the pipelines' live changes, says every
/// refusal of a write in an alert with "Reload" (403, 404, 409) and offers "Undo" after a write.
/// </summary>
public partial class PipelineSchedule : IDisposable
{
    /// <summary>How many days ahead the plan looks.</summary>
    internal const int WindowDays = 7;

    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ActiveOrganizationService Organizations { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;

    private readonly TrailingReloadCoalescer _liveReload = new(1000);
    private SchedulePlanDto? _plan;
    private SchedulePlanSummary? _summary;
    private (HttpStatusCode Status, string? Message)? _failure;
    private bool _onlyOverlaps;
    private int _loadVersion;
    private bool _disposed;

    /// <summary>The genre filter in the URL (<c>?kind=</c>, see <see cref="PipelineScheduleFilter"/>).</summary>
    [Parameter, SupplyParameterFromQuery(Name = PipelineScheduleFilter.KindKey)] public string? KindQuery { get; set; }

    /// <summary>The server filter in the URL (<c>?server=</c>).</summary>
    [Parameter, SupplyParameterFromQuery(Name = PipelineScheduleFilter.ServerKey)] public string? ServerQuery { get; set; }

    private string? Kind => PipelineScheduleFilter.ParseKind(KindQuery);
    private int? ServerFilter => PipelineScheduleFilter.ParseServer(ServerQuery);
    private bool IsFiltered => Kind is not null || ServerFilter is not null;

    // The plan the calendar and the cards draw: the page's plan reduced to the chosen genre and server. Rebuilt only when the
    // plan or the choice changes, so the calendar sees the same instance between two renders and does not load again.
    private SchedulePlanDto? _viewPlan;
    private SchedulePlanDto? _viewPlanOf;
    private string? _viewPlanKind;
    private int? _viewPlanServer;

    private SchedulePlanDto? ViewPlan
    {
        get
        {
            if (_plan is null) return null;
            if (!ReferenceEquals(_viewPlanOf, _plan) || _viewPlanKind != Kind || _viewPlanServer != ServerFilter)
            {
                _viewPlan = PipelineScheduleFilter.Apply(_plan, Kind, ServerFilter);
                _viewPlanOf = _plan;
                _viewPlanKind = Kind;
                _viewPlanServer = ServerFilter;
            }

            return _viewPlan;
        }
    }

    // Nothing to list: the overlap tile, a filter or an empty organization each have their own words.
    private string EmptyTitleKey => _onlyOverlaps ? "ScheduleNoOverlapsTitle" : IsFiltered ? "ScheduleFilteredEmptyTitle" : "ScheduleEmptyTitle";

    private string EmptyDescriptionKey => _onlyOverlaps ? "ScheduleNoOverlapsDescription" : IsFiltered ? "ScheduleFilteredEmptyDescription" : "ScheduleEmptyDescription";

    private TimeZoneInfo Zone => Clock.LocalTimeZone;
    private DateTime NowUtc => Clock.GetUtcNow().UtcDateTime;

    private string TimeHelp => string.Format(L["ScheduleTimeHelpPage"].Value, ScheduleLocalTime.ZoneName(Zone));

    /// <summary>The cards: every scheduled pipeline, or only the ones that share time with another when the
    /// overlap tile was chosen. Switched-off ones come after the active ones.</summary>
    private IReadOnlyList<ScheduledTaskDto> VisibleTasks => _plan is null || _summary is null
        ? []
        : [.. ViewPlan!.Tasks
            .Where(task => !_onlyOverlaps || _summary.HasOverlap(task.PipelineId))
            .OrderByDescending(task => task.Enabled)
            .ThenBy(task => task.NextRunUtc ?? DateTime.MaxValue)
            .ThenBy(task => task.PipelineName, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The cards of the servers' scheduled actions (R-8), after the pipelines': the next to run first, or only
    /// the ones that share time with another run when the overlap tile was chosen.</summary>
    private IReadOnlyList<ScheduledServerActionDto> VisibleServerActions => _plan is null || _summary is null
        ? []
        : [.. ViewPlan!.ServerActions
            .Where(action => !_onlyOverlaps || _summary.HasOverlap(ServerActionPlannerTask.RowId(action)))
            .OrderBy(action => action.NextRunUtc ?? DateTime.MaxValue)
            .ThenBy(action => ServerActionPlannerTask.Title(L, action), StringComparer.CurrentCultureIgnoreCase)];

    private bool CanSchedule => Permissions.CanWrite(ResourceType.Pipeline)
        || Permissions.GetPermissions().Any(permission => permission.ResourceType == ResourceType.Pipeline && permission.Permission >= Permission.Write);

    private bool CanWrite(ScheduledTaskDto task) => Permissions.CanWrite(ResourceType.Pipeline, task.PipelineId);

    protected override async Task OnInitializedAsync()
    {
        FollowPermissions();
        Organizations.Changed += OnOrganizationChanged;
        await LoadAsync();
        // A pipeline's definition is the schedule: a change made elsewhere (the editor, a pushed file) reads again.
        await FollowEntitiesAsync(ResourceType.Pipeline);
    }

    protected override Task OnEntitiesChangedAsync() => _liveReload.RequestAsync(LoadAsync);

    protected override void OnPermissionsChanged() => _ = InvokeAsync(StateHasChanged);

    private void OnOrganizationChanged() => _ = InvokeAsync(async () =>
    {
        _plan = null;
        _summary = null;
        _failure = null;
        await LoadAsync();
        StateHasChanged();
    });

    /// <summary>Reads the plan of the active organization. The last read wins: a slower answer to an earlier
    /// request is dropped, as is one that arrives after the organization changed.</summary>
    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        if (Organizations.Active is not { } organization) return;
        var now = NowUtc;
        // R-43: from the start of the week (or of the night in progress) so the calendar draws from this plan.
        var (from, to) = SchedulePlanWindow.For(now, Zone, WindowDays, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
        ScheduleCall<SchedulePlanDto> call;
        try
        {
            call = await Api.PipelineSchedules.GetPlanAsync(organization.Id, from, to);
        }
        catch (HttpRequestException)
        {
            if (version == _loadVersion) _failure = (HttpStatusCode.ServiceUnavailable, L["ScheduleLoadFailed"].Value);
            return;
        }

        if (version != _loadVersion || _disposed) return;
        if (call.IsSuccess)
        {
            _plan = call.Value;
            _summary = SchedulePlanSummary.Build(call.Value!, Zone, now, action => ServerActionPlannerTask.Title(L, action));
            _failure = null;
            return;
        }

        _plan = null;
        _summary = null;
        _failure = (call.Status, call.Message);
    }

    private async Task ReloadAsync()
    {
        _failure = null;
        await LoadAsync();
    }

    /// <summary>The plan of a range the calendar shows outside the page's own window (another night or week).
    /// The page's plan, tiles and cards stay as they are; a refusal is said in the page's alert.</summary>
    private async Task<SchedulePlanDto?> LoadRangeAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        if (Organizations.Active is not { } organization) return null;
        ScheduleCall<SchedulePlanDto> call;
        try
        {
            call = await Api.PipelineSchedules.GetPlanAsync(organization.Id, from, to, ct: ct);
        }
        catch (HttpRequestException)
        {
            call = new ScheduleCall<SchedulePlanDto>(null, HttpStatusCode.ServiceUnavailable, L["ScheduleLoadFailed"].Value);
        }

        if (call.IsSuccess) return call.Value is { } loaded ? PipelineScheduleFilter.Apply(loaded, Kind, ServerFilter) : null;
        await InvokeAsync(() =>
        {
            _failure = (call.Status, call.Message);
            StateHasChanged();
        });
        return null;
    }

    /// <summary>A drop on the calendar that changes a schedule: the same write as the cards' menu, under the same
    /// veil, with the same refusal alert and the same "Undo".</summary>
    private Task WriteMoveAsync(ScheduleMoveWrite move) =>
        WriteAsync(move.Task.PipelineId, move.Task.PipelineName, move.Request, move.Detail, move.Undo);

    /// <summary>A drop of a scheduled action's block (R-8): its schedule is written once where it is kept, under the same
    /// veil, and the plan is read again. The endpoint of its sort checks the right again; a refusal is said in a toast.
    /// "Undo" stores the previous expression back.</summary>
    private Task WriteServerActionMoveAsync(ServerActionMoveWrite move)
    {
        var title = ServerActionPlannerTask.Title(L, move.Action);
        return StoreCronAsync(move.Action, move.CronExpression, move.Detail, () => UndoServerActionAsync(move, title));
    }

    private Task UndoServerActionAsync(ServerActionMoveWrite move, string title) =>
        // A toast's button is not an event of this page: nothing redraws it unless this does.
        InvokeAsync(async () =>
        {
            await StoreCronAsync(move.Action, move.UndoCronExpression, string.Format(L["ScheduleUndoneDetail"].Value, title), undo: null);
            StateHasChanged();
        });

    /// <summary>Stores the schedule of an action through the endpoint of its sort (which checks the right again, see
    /// <see cref="ScheduledActionWriter"/>), reads the plan again, then says the success (<paramref name="detail"/>, with
    /// "Undo" when <paramref name="undo"/> is set) or the refusal in a toast.</summary>
    private async Task StoreCronAsync(ScheduledServerActionDto action, string cron, string detail, Func<Task>? undo)
    {
        var stored = false;
        try
        {
            await Veil.RunAsync(async () =>
            {
                stored = await ScheduledActionWriter.StoreAsync(Api, action, cron);
                if (!stored) return;
                _failure = null;
                await LoadAsync();
            });
        }
        catch (HttpRequestException)
        {
            _failure = (HttpStatusCode.ServiceUnavailable, L["ScheduleLoadFailed"].Value);
            return;
        }

        if (stored && undo is not null) Notify.Notify(OmniSeverity.Success, "ScheduleSaved", detail, "Undo", undo);
        else if (stored) Notify.Notify(OmniSeverity.Success, "ScheduleSaved", detail);
        else Notify.Error("Error", "SaveFailed");
    }

    /// <summary>The overlap tile (R-26): choosing it keeps only the pipelines that share time and shows the night, where the
    /// overlaps are drawn, by putting the night view in the URL (the genre, server and day already there are kept); choosing it
    /// again shows everything and leaves the view as it is.</summary>
    private void ToggleOverlapFilter()
    {
        _onlyOverlaps = !_onlyOverlaps;
        if (_onlyOverlaps)
            Nav.NavigateTo(Nav.GetUriWithQueryParameters(new Dictionary<string, object?> { [PipelineSchedulePlanUrl.ViewKey] = SchedulePlanViewNames.Night }));
    }

    /// <summary>The reader chose the branch a pipeline's definition is read on (recette R-5, user decision 2026-11-09): after a
    /// confirmation naming the old and the new branch, the pipeline's real source branch is stored and the plan is read again, so
    /// the watch uses it from now on. Declining leaves everything as it is. A refusal or a failed save is said in an error toast and
    /// nothing changes; the card puts its selector back on the stored branch whatever the answer.</summary>
    private async Task ChooseSourceBranchAsync(SourceBranchChoice choice)
    {
        var task = _plan?.Tasks.FirstOrDefault(item => item.PipelineId == choice.PipelineId);
        if (task is null || string.Equals(choice.Branch, task.OwnSourceBranch, StringComparison.Ordinal)) return;
        var oldBranch = PipelineScheduleSourceText.BranchName(L, task.OwnSourceBranch);
        var confirmed = await Dialog.Confirm(
            string.Format(L["ScheduleSourceChangeConfirm"].Value, task.PipelineName, oldBranch, choice.Branch),
            L["ScheduleSourceChangeTitle"].Value,
            new OmniConfirmOptions { OkButtonText = L["ScheduleSourceChangeAction"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        ScheduleCall<PipelineDto>? call = null;
        try
        {
            await Veil.RunAsync(async () =>
            {
                call = await Api.PipelineSchedules.UpdateSourceBranchAsync(
                    choice.PipelineId, new UpdatePipelineSourceBranchRequest { Branch = choice.Branch, ExpectedBranch = task.OwnSourceBranch });
                if (call.IsSuccess) await LoadAsync();
            });
        }
        catch (HttpRequestException)
        {
            call = new ScheduleCall<PipelineDto>(null, HttpStatusCode.ServiceUnavailable, null);
        }

        if (call is { IsSuccess: true })
        {
            Notify.Notify(OmniSeverity.Success, "ScheduleSourceChanged",
                string.Format(L["ScheduleSourceChangedDetail"].Value, task.PipelineName, choice.Branch));
            return;
        }

        Notify.Notify(OmniSeverity.Danger, "ScheduleSourceChangeFailed",
            call?.Message ?? string.Format(L["ScheduleSourceChangeFailedDetail"].Value, task.PipelineName, choice.Branch));
    }

    private async Task OpenDialogAsync(ScheduledTaskDto? item)
    {
        if (Organizations.Active is not { } organization) return;
        var result = await Dialog.OpenAsync<PipelineScheduleDialog>(
            L[item is null ? "SchedulePipelineTitle" : "ScheduleEditFrequency"].Value,
            new Dictionary<string, object?>
            {
                ["Item"] = item,
                ["OrganizationId"] = organization.Id,
                ["ScheduledPipelineIds"] = (IReadOnlyCollection<int>)(_plan?.Tasks.Select(task => task.PipelineId).ToList() ?? []),
                ["WindowDays"] = WindowDays,
                ["OnUndo"] = (Func<PipelineScheduleDialogResult, Task>)UndoSavedAsync
            },
            new OmniDialogOptions { Width = "620px", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });
        // The dialog wrote the schedule and said so itself (toast with "Undo"); the page only reads the plan again,
        // also after a refusal the reader chose to reload.
        if (result is PipelineScheduleDialogResult) await LoadAsync();
    }

    /// <summary>The "Undo" of a schedule the dialog saved: puts the previous value back.</summary>
    private Task UndoSavedAsync(PipelineScheduleDialogResult saved) =>
        saved.Undo is null ? Task.CompletedTask : UndoAsync(saved.PipelineId, saved.PipelineName, saved.Undo);

    private Task OpenEditAsync(ScheduledTaskDto item) => OpenDialogAsync(item);

    /// <summary>Switches a schedule off or back on, the value kept in the YAML.</summary>
    private Task ToggleAsync(ScheduledTaskDto item)
    {
        var enabled = !item.Enabled;
        var request = new UpdatePipelineScheduleRequest
        {
            Frequency = null,
            Enabled = enabled,
            ExpectedCronExpression = item.CronExpression,
            ExpectedEnabled = item.Enabled
        };
        return WriteAsync(item.PipelineId, item.PipelineName, request,
            string.Format(L[enabled ? "ScheduleSwitchedOnDetail" : "ScheduleSwitchedOffDetail"].Value, item.PipelineName),
            new UpdatePipelineScheduleRequest { Frequency = null, Enabled = item.Enabled });
    }

    /// <summary>One write under the application's veil. A refusal stays in the alert (403, 404 and 409 with
    /// "Reload"), a success reads the plan again and offers "Undo".</summary>
    private async Task WriteAsync(int pipelineId, string pipelineName, UpdatePipelineScheduleRequest request, string detail, UpdatePipelineScheduleRequest? undo)
    {
        ScheduleCall<PipelineDto>? call = null;
        try
        {
            // R-43: one veil from the write to the plan read again, so the reader sees the block where it now is when the
            // veil lifts, not the old block for the time of a second read.
            await Veil.RunAsync(async () =>
            {
                call = await Api.PipelineSchedules.UpdateAsync(pipelineId, request);
                if (!call.IsSuccess) return;
                _failure = null;
                await LoadAsync();
            });
        }
        catch (HttpRequestException)
        {
            _failure = (HttpStatusCode.ServiceUnavailable, L["ScheduleLoadFailed"].Value);
            return;
        }

        if (call is null) return;
        if (!call.IsSuccess)
        {
            _failure = (call.Status, call.Message);
            return;
        }

        // The toast says what was written, with "Undo" when the previous value can be written back.
        if (undo is null)
            Notify.Notify(OmniSeverity.Success, "ScheduleSaved", detail);
        else
            Notify.Notify(OmniSeverity.Success, "ScheduleSaved", detail, "Undo", () => UndoAsync(pipelineId, pipelineName, undo));
        if (call.Value!.OwnerInactive)
            Notify.Warning("PipelineOwnerInactive", "PipelineOwnerInactiveHint", call.Value.OwnerUsername ?? L["PipelineOwnerMissing"].Value);
    }

    /// <summary>Puts the previous value back. What it expects is what the page last read, so a schedule changed
    /// by someone else since is refused (409) instead of overwritten.</summary>
    private Task UndoAsync(int pipelineId, string pipelineName, UpdatePipelineScheduleRequest restore)
    {
        var current = _plan?.Tasks.FirstOrDefault(task => task.PipelineId == pipelineId);
        var request = new UpdatePipelineScheduleRequest
        {
            Frequency = restore.Frequency,
            Day = restore.Day,
            Hour = restore.Hour,
            Minute = restore.Minute,
            Enabled = restore.Enabled,
            ExpectedCronExpression = current?.CronExpression ?? string.Empty,
            ExpectedEnabled = current?.Enabled ?? false
        };
        // A toast's button is not an event of this page: nothing redraws it unless this does.
        return InvokeAsync(async () =>
        {
            await WriteAsync(pipelineId, pipelineName, request,
                string.Format(L["ScheduleUndoneDetail"].Value, pipelineName), undo: null);
            StateHasChanged();
        });
    }

    private string TasksDetail => _summary!.DisabledCount > 0
        ? string.Format(L["ScheduleTileTasksOff"].Value, _summary.DisabledCount)
        : L["ScheduleTileAllActive"].Value;

    private string TonightDetail => string.Format(L["ScheduleTileTonightDetail"].Value,
        SchedulePhrases.Time(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_summary!.NightStartUtc, Zone))),
        SchedulePhrases.Time(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_summary.NightEndUtc, Zone))));

    private string OverlapsDetail
    {
        get
        {
            if (_onlyOverlaps) return L["ScheduleTileOverlapsFiltered"].Value;
            if (_summary!.Overlaps.Count == 0)
            {
                // R-39: with room for several runs at once, "none" says how many may run together.
                return _plan!.ExecutionCapacity > 1
                    ? string.Format(L["ScheduleTileOverlapsNoneCapacity"].Value, WindowDays, _plan.ExecutionCapacity)
                    : string.Format(L["ScheduleTileOverlapsNone"].Value, WindowDays);
            }

            return string.Format(L["ScheduleTileOverlapsSome"].Value,
                    _summary.Overlaps[0].First.PipelineName, _summary.Overlaps[0].Second.PipelineName, SchedulePhrases.Minutes(_summary.Overlaps[0].Minutes));
        }
    }

    private string NextValue => _summary!.Next is { } next
        ? SchedulePhrases.Time(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(next.AtUtc, Zone)))
        : "-";

    private string NextDetail => _summary!.Next is { } next
        ? string.Format(L["ScheduleTileNextDetail"].Value, next.Task.PipelineName, SchedulePhrases.Minutes(Math.Max(1, (int)Math.Round((next.AtUtc - NowUtc).TotalMinutes))))
        : L["ScheduleNoNextRun"].Value;

    public override async ValueTask DisposeAsync()
    {
        Dispose();
        await base.DisposeAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Organizations.Changed -= OnOrganizationChanged;
    }
}
