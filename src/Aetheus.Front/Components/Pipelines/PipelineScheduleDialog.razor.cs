// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The form of "Schedule a pipeline" and "Change frequency": a frequency in words and a local time, written as
/// UTC. The preview endpoint is asked on every change (the last answer wins) for the next runs and the overlap
/// verdict, which warns and never blocks. The dialog does the write itself and says a refusal in place: a 400
/// stays on the form, a 403, 404 or 409 offers "Reload", which closes the dialog so the page reads the plan again.
/// </summary>
public partial class PipelineScheduleDialog
{
    private static readonly ScheduleFrequency[] Frequencies =
    [
        ScheduleFrequency.EveryEvening, ScheduleFrequency.Weekdays, ScheduleFrequency.EveryTwoDays,
        ScheduleFrequency.Weekly, ScheduleFrequency.Monthly
    ];

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;

    /// <summary>The scheduled task to change; null to schedule a pipeline that is not scheduled yet.</summary>
    [Parameter] public ScheduledTaskDto? Item { get; set; }

    /// <summary>The organization whose pipelines the form offers when creating.</summary>
    [Parameter] public int OrganizationId { get; set; }

    /// <summary>The pipelines that already have a schedule (switched off or not): not offered when creating.</summary>
    [Parameter] public IReadOnlyCollection<int> ScheduledPipelineIds { get; set; } = [];

    /// <summary>The days the preview looks ahead, the plan's window.</summary>
    [Parameter] public int WindowDays { get; set; } = 7;

    /// <summary>What "Undo" does once a schedule is saved: the page puts the previous value back. Without it the
    /// success toast offers no undo.</summary>
    [Parameter] public Func<PipelineScheduleDialogResult, Task>? OnUndo { get; set; }

    private readonly EditModel _model = new();
    private IReadOnlyList<OmniOption<int?>> _pipelineOptions = [];
    private IReadOnlyList<OmniOption<ScheduleFrequency>> _frequencyOptions = [];
    private IReadOnlyList<OmniOption<int>> _weekdayOptions = [];
    private IReadOnlyList<OmniOption<int>> _monthDayOptions = [];
    private List<PipelineFleetItemDto> _candidates = [];
    private SchedulePreviewDto? _preview;
    private string? _previewError;
    private (HttpStatusCode Status, string? Message)? _refusal;
    private int _previewVersion;
    private bool _loading;
    private bool _loadFailed;
    private bool _busy;
    private bool _dayImpossible;
    private bool _isCustom;

    private bool Creating => Item is null;
    private TimeZoneInfo Zone => Clock.LocalTimeZone;
    private DateTime NowUtc => Clock.GetUtcNow().UtcDateTime;

    private bool CanSave => !_busy && !_dayImpossible && _previewError is null && _preview is { YamlError: null };

    private string TimeHelp => string.Format(L["ScheduleTimeHelp"].Value, ScheduleLocalTime.ZoneName(Zone));

    protected override async Task OnInitializedAsync()
    {
        _frequencyOptions = [.. Frequencies.Select(frequency => new OmniOption<ScheduleFrequency>(frequency, L.Localize(frequency)))];
        _weekdayOptions = [.. new[] { 1, 2, 3, 4, 5, 6, 0 }.Select(day => new OmniOption<int>(day, SchedulePhrases.DayName(day)))];
        _monthDayOptions = [.. Enumerable.Range(1, ScheduleFrequencyCodec.MaxMonthDay).Select(day => new OmniOption<int>(day, day.ToString(System.Globalization.CultureInfo.CurrentCulture)))];

        if (Item is { } item)
        {
            ReadTask(item);
            _pipelineOptions = [new OmniOption<int?>(item.PipelineId, item.PipelineName)];
            await RefreshPreviewAsync();
            return;
        }

        _loading = true;
        try
        {
            _candidates = await ReadCandidatesAsync();
            _pipelineOptions = [.. _candidates.Select(candidate => new OmniOption<int?>(candidate.PipelineId, candidate.PipelineName))];
        }
        catch (HttpRequestException)
        {
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The form of a task as the reader's clock says it. A custom expression has no named
    /// frequency to start from: the form starts on "every evening" and says that saving replaces it.</summary>
    private void ReadTask(ScheduledTaskDto item)
    {
        _model.PipelineId = item.PipelineId;
        _isCustom = item.Frequency == ScheduleFrequency.Custom;
        if (!_isCustom) _model.Frequency = item.Frequency;
        if (ScheduleLocalTime.ToLocal(item, Zone, NowUtc) is { } local)
        {
            _model.Time = local.Time;
            if (item.Frequency == ScheduleFrequency.Weekly && local.Day is { } weekday) _model.Weekday = weekday;
            if (item.Frequency == ScheduleFrequency.Monthly && local.Day is { } monthDay) _model.MonthDay = monthDay;
        }
    }

    /// <summary>The pipelines of the organization that have no schedule: the fleet lists every pipeline the
    /// reader may read, page by page; the organization and the already scheduled are filtered here.</summary>
    private async Task<List<PipelineFleetItemDto>> ReadCandidatesAsync()
    {
        var all = new List<PipelineFleetItemDto>();
        for (var page = 1; page <= 50; page++)
        {
            var result = await Api.Pipelines.GetPipelineFleetAsync(page, 100);
            all.AddRange(result.Items);
            if (result.Items.Count == 0 || all.Count >= result.TotalCount) break;
        }

        return [.. all
            .Where(item => item.OrganizationId == OrganizationId && !ScheduledPipelineIds.Contains(item.PipelineId))
            .OrderBy(item => item.PipelineName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The proposal in UTC, or null when no pipeline is chosen or the local day cannot be written.</summary>
    private ScheduleFrequencySpec? BuildSpec()
    {
        _dayImpossible = false;
        if (_model.PipelineId is null || _model.Time is not { } time) return null;
        var localDay = _model.Frequency switch
        {
            ScheduleFrequency.Weekly => _model.Weekday,
            ScheduleFrequency.Monthly => _model.MonthDay,
            _ => (int?)null
        };
        var spec = ScheduleLocalTime.ToUtc(_model.Frequency, localDay, time, Zone, NowUtc);
        _dayImpossible = spec is null;
        return spec;
    }

    private async Task RefreshPreviewAsync()
    {
        var version = ++_previewVersion;
        _previewError = null;
        var spec = BuildSpec();
        if (spec is null || _model.PipelineId is not { } pipelineId)
        {
            _preview = null;
            return;
        }

        ScheduleCall<SchedulePreviewDto> call;
        try
        {
            call = await Api.PipelineSchedules.GetPreviewAsync(pipelineId, spec, NowUtc, NowUtc.AddDays(WindowDays));
        }
        catch (HttpRequestException)
        {
            if (version == _previewVersion) { _preview = null; _previewError = L["ScheduleLoadFailed"].Value; }
            return;
        }

        // A later change started another preview: only the last answer is shown.
        if (version != _previewVersion) return;
        if (call.IsSuccess)
        {
            _preview = call.Value;
            return;
        }

        _preview = null;
        if (call.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            _refusal = (call.Status, call.Message);
        else
            _previewError = string.IsNullOrWhiteSpace(call.Message) ? L["ScheduleLoadFailed"].Value : call.Message;
    }

    private IEnumerable<string> NextRuns(SchedulePreviewDto preview) =>
        preview.Proposal.Occurrences.OrderBy(run => run.StartUtc).Take(3).Select(run => SchedulePhrases.DateTime(run.StartUtc, Zone));

    private string OverlapVerdict(SchedulePreviewDto preview)
    {
        var first = preview.Overlaps.OrderByDescending(overlap => overlap.OverlapMinutes).First();
        return string.Format(L["ScheduleVerdictOverlap"].Value, first.PipelineName, SchedulePhrases.Minutes(first.OverlapMinutes));
    }

    private async Task SaveAsync()
    {
        if (BuildSpec() is not { } spec || _model.PipelineId is not { } pipelineId) return;
        var request = RequestFor(spec);

        _busy = true;
        _refusal = null;
        ScheduleCall<PipelineDto>? call = null;
        try
        {
            await Veil.RunAsync(async () => call = await Api.PipelineSchedules.UpdateAsync(pipelineId, request));
        }
        catch (HttpRequestException)
        {
            _refusal = (default, L["ScheduleLoadFailed"].Value);
            return;
        }
        finally
        {
            _busy = false;
        }

        if (call is null) return;
        if (!call.IsSuccess)
        {
            _refusal = (call.Status, call.Message);
            return;
        }

        var pipeline = call.Value!;
        var saved = ResultOf(pipelineId, pipeline);
        // The toast outlives the dialog: its "Undo" is handed by the page that opened it.
        var sentence = string.Format(L["ScheduleSavedDetail"].Value, saved.PipelineName, saved.Summary);
        if (saved.Undo is not null && OnUndo is { } undo)
            Notify.Notify(OmniSeverity.Success, "ScheduleSaved", sentence, "Undo", () => undo(saved));
        else
            Notify.Notify(OmniSeverity.Success, "ScheduleSaved", sentence);
        if (pipeline.OwnerInactive)
            Notify.Warning("PipelineOwnerInactive", "PipelineOwnerInactiveHint", pipeline.OwnerUsername ?? L["PipelineOwnerMissing"].Value);
        Dialog.Close(saved);
    }

    private UpdatePipelineScheduleRequest RequestFor(ScheduleFrequencySpec spec) => new()
    {
        Frequency = spec.Frequency,
        Day = spec.Day,
        Hour = spec.Hour,
        Minute = spec.Minute,
        Enabled = Item?.Enabled ?? true,
        ExpectedCronExpression = Item?.CronExpression ?? string.Empty,
        ExpectedEnabled = Item?.Enabled ?? false
    };

    private PipelineScheduleDialogResult ResultOf(int pipelineId, PipelineDto pipeline)
    {
        var localDay = _model.Frequency switch
        {
            ScheduleFrequency.Weekly => _model.Weekday,
            ScheduleFrequency.Monthly => _model.MonthDay,
            _ => (int?)null
        };
        return new PipelineScheduleDialogResult(
            Saved: true, Reload: false, pipelineId, pipeline.Name,
            SchedulePhrases.Frequency(L, _model.Frequency, localDay, _model.Time ?? default),
            pipeline.OwnerUsername, pipeline.OwnerInactive, UndoOf());
    }

    /// <summary>The request that puts the previous value back: switching a freshly scheduled pipeline off, or the
    /// previous named frequency. A custom expression cannot be written, so it offers no undo.</summary>
    private UpdatePipelineScheduleRequest? UndoOf()
    {
        if (Item is null) return new UpdatePipelineScheduleRequest { Frequency = null, Enabled = false };
        return ScheduleMovePlanner.Previous(Item);
    }

    private void CloseForReload() => Dialog.Close(new PipelineScheduleDialogResult(Saved: false, Reload: true));

    private void Cancel() => Dialog.Close(null);

    private sealed class EditModel
    {
        [Required]
        public int? PipelineId { get; set; }

        public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.EveryEvening;
        public int Weekday { get; set; } = 1;
        public int MonthDay { get; set; } = 1;

        [Required]
        public TimeOnly? Time { get; set; } = new TimeOnly(2, 0);
    }
}
