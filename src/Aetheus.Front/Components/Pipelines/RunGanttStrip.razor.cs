// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The shared time axis exposes actual stage overlap and compares durations with
/// successful runs. <see cref="RunGanttChart"/> and <see cref="RunGanttProgress"/> compute
/// placement and progress independently of rendering.</summary>
public partial class RunGanttStrip
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunDto? Run { get; set; }

    /// <summary>Usual durations of this run's pipeline; null while unknown or unavailable, in which
    /// case the average and last columns are simply not drawn.</summary>
    [Parameter] public RunStageBaselinesDto? Baselines { get; set; }

    /// <summary>Injected so a run in progress can be laid out against a clock a test controls; the
    /// bars of a running stage otherwise depend on the wall clock.</summary>
    [Parameter] public TimeProvider? Clock { get; set; }

    internal RunGanttLayout? Layout { get; private set; }

    /// <summary>Numbers each strip so its hatch pattern id is unique on the page.</summary>
    private static int _instances;
    private readonly int _instance = Interlocked.Increment(ref _instances);

    private string WaitPatternId => $"run-gantt-wait-hatch-{_instance}";
    private string WaitPatternFill => $"url(#{WaitPatternId})";

    internal enum GanttColumn { Duration, Wait, Average, Last, Cpu, Io }

    /// <summary>One labelled tick per quarter of the axis.</summary>
    internal sealed record Tick(int Percent, string Label);

    internal sealed record Column(
        GanttColumn Kind,
        string TitleKey,
        string HintKey,
        Func<RunGanttBar, RunGanttLayout, string> Value);

    /// <summary>The instant the layout was built against; a running step's bar reaches it too, so a
    /// step and its stage never disagree about "now".</summary>
    private DateTime _now;

    /// <summary>Stages whose steps are unfolded. Keyed by kind and name rather than by view model,
    /// so a stage stays open while the run refreshes live. Collapsed by default.</summary>
    private readonly HashSet<string> _expandedStages = new(StringComparer.Ordinal);

    protected override void OnParametersSet()
    {
        // The bars are placed from the steps' timestamps: "now" takes their kind.
        _now = NowFor(Clock, Run?.Steps.FirstOrDefault(step => step.StartedAt is not null)?.StartedAt ?? Run?.StartedAt ?? default);
        Layout = RunGanttChart.Build(RunStageBuilder.Build(Run), _now);
    }

    private static string StageKey(RunGanttBar bar) =>
        (bar.Stage.IsSystem ? "system:" : "stage:") + bar.Stage.Name;

    internal bool IsExpanded(RunGanttBar bar) => _expandedStages.Contains(StageKey(bar));

    private void Toggle(RunGanttBar bar)
    {
        var key = StageKey(bar);
        if (!_expandedStages.Remove(key)) _expandedStages.Add(key);
    }

    private string ToggleText(RunGanttBar bar, bool expanded) =>
        string.Format(CultureInfo.CurrentCulture,
            L[expanded ? "GanttHideSteps" : "GanttShowSteps"], bar.Stage.DisplayName);

    /// <summary>Id of a stage's step group, for the toggle's aria-controls: unique per strip instance
    /// (like the hatch pattern) and per row.</summary>
    private string StepsId(RunGanttBar bar)
    {
        var index = 0;
        if (Layout is not null)
        {
            for (; index < Layout.Bars.Count; index++)
                if (ReferenceEquals(Layout.Bars[index], bar)) break;
        }
        return $"run-gantt-steps-{_instance}-{index}";
    }

    /// <summary>Start, end and duration of one step, one fact per line.</summary>
    internal string StepTitle(RunGanttStepBar step)
    {
        var culture = CultureInfo.CurrentCulture;
        var lines = new List<string>
        {
            step.Label + (step.IsRunning ? $" ({L["Running"]})" : string.Empty),
            string.Format(culture, L["GanttTooltipStart"], step.Step.StartedAt!.Value.ToString("T", culture)),
            step.Step.CompletedAt is { } end
                ? string.Format(culture, L["GanttTooltipEnd"], end.ToString("T", culture))
                : string.Format(culture, L["GanttTooltipEnd"], L["Running"]),
            string.Format(culture, L["GanttTooltipDuration"], FormatDuration(step.Duration))
        };
        return string.Join('\n', lines);
    }

    /// <summary>The columns at least one stage has a value for, in their fixed order. Duration always
    /// has one, since every bar started.</summary>
    internal List<Column> VisibleColumns(RunGanttLayout layout)
    {
        Column[] all =
        [
            new(GanttColumn.Duration, "GanttColumnDuration", "GanttColumnDurationHint",
                StageDuration),
            // A stage that started at once has no wait to show; when none waited, the column is not
            // drawn, like the others.
            new(GanttColumn.Wait, "GanttColumnWait", "GanttColumnWaitHint",
                (bar, _) => bar.Wait >= TimeSpan.FromSeconds(1) ? FormatDuration(bar.Wait) : string.Empty),
            new(GanttColumn.Average, "GanttColumnAverage", "GanttColumnAverageHint",
                (bar, _) => Seconds(Baseline(bar)?.AverageSeconds)),
            new(GanttColumn.Last, "GanttColumnLast", "GanttColumnLastHint",
                (bar, _) => Seconds(Baseline(bar)?.LastSeconds)),
            new(GanttColumn.Cpu, "GanttColumnCpu", "GanttColumnCpuHint",
                (bar, _) => Seconds(Usage(bar)?.CpuSeconds)),
            new(GanttColumn.Io, "GanttColumnIo", "GanttColumnIoHint",
                (bar, _) => Usage(bar)?.DiskBytes is { } bytes and > 0
                    ? PipelineRunFormatting.FormatSize((long)bytes)
                    : string.Empty)
        ];
        return [.. all.Where(column => layout.Bars.Any(bar => column.Value(bar, layout).Length > 0))];
    }

    private StageBaselineDto? Baseline(RunGanttBar bar) =>
        Baselines?.Stages.FirstOrDefault(stage =>
            string.Equals(stage.StageName, bar.Stage.Name, StringComparison.OrdinalIgnoreCase)
            && stage.IsSystem == bar.Stage.IsSystem);

    private PipelineRunStepResources.Usage? Usage(RunGanttBar bar) =>
        PipelineRunStepResources.ForStage(Run, bar.Stage.Name);

    /// <summary>Only a stage still running gets a fill: a finished stage has its duration in the
    /// column beside it, and a fill there would read as progress that is not happening.</summary>
    internal RunGanttProgress? Progress(RunGanttBar bar, RunGanttLayout layout) =>
        bar.IsRunning
            ? RunGanttProgress.Of(Elapsed(bar, layout), Baseline(bar)?.AverageSeconds)
            : null;

    private static string SvgNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double ScaledWidth(double barWidth, double fillPercent) =>
        barWidth * Math.Clamp(fillPercent, 0, 100) / 100;

    private static string BarClass(RunGanttBar bar) => StatusClass(bar.Stage);

    // Recette R1-80: a stage or step whose control ran and found failures is Rejected, in the warning
    // colour, not a green success.
    private const string RejectedClass = "run-gantt-rejected";

    private static string StatusClass(StageViewModel stage) => stage.IsRejected ? RejectedClass : StatusClass(stage.Status);

    private static string StatusClass(PipelineStepRunDto step) => step.IsRejected ? RejectedClass : StatusClass(step.Status);

    private static string StatusClass(TaskExecutionStatus status) => status switch
    {
        TaskExecutionStatus.Success => "run-gantt-success",
        TaskExecutionStatus.Failed => "run-gantt-failed",
        TaskExecutionStatus.Running => "run-gantt-running",
        TaskExecutionStatus.Cancelled => "run-gantt-cancelled",
        _ => "run-gantt-other"
    };

    /// <summary>A running stage has no recorded end, so its duration runs to the axis's right edge,
    /// which is the same "now" the layout was built against. Reading the clock again here would make
    /// the number disagree with the bar it labels.</summary>
    private static TimeSpan Elapsed(RunGanttBar bar, RunGanttLayout layout) =>
        (bar.Stage.CompletedAt ?? layout.Start + layout.Duration) - bar.Stage.StartedAt!.Value;

    private static string StageDuration(RunGanttBar bar, RunGanttLayout layout) =>
        FormatDuration(Elapsed(bar, layout));

    /// <summary>Start, end, duration, the wait before the stage and, when this pipeline has a usual
    /// duration for it, how far this run is from it. One fact per line.</summary>
    internal string BarTitle(RunGanttBar bar, RunGanttLayout layout, RunGanttProgress? progress)
    {
        var culture = CultureInfo.CurrentCulture;
        var lines = new List<string>
        {
            bar.Stage.DisplayName + (bar.IsRunning ? $" ({L["Running"]})" : string.Empty),
            string.Format(culture, L["GanttTooltipStart"], bar.Stage.StartedAt!.Value.ToString("T", culture)),
            bar.Stage.CompletedAt is { } end
                ? string.Format(culture, L["GanttTooltipEnd"], end.ToString("T", culture))
                : string.Format(culture, L["GanttTooltipEnd"], L["Running"]),
            string.Format(culture, L["GanttTooltipDuration"], StageDuration(bar, layout)),
            string.Format(culture, L["GanttTooltipWait"], FormatDuration(bar.Wait))
        };

        if (Baseline(bar)?.AverageSeconds is double average && average > 0)
        {
            var usual = TimeSpan.FromSeconds(average);
            lines.Add(string.Format(culture, L["GanttTooltipDeviation"],
                SignedDuration(Elapsed(bar, layout) - usual), FormatDuration(usual)));
        }

        if (progress is { IsOverrunning: true }) lines.Add(L["GanttOverAverage"]);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// FRONT-013: "now" on the server's clock (<see cref="ServerClock"/>, or the clock a test injects),
    /// in the same <see cref="DateTimeKind"/> as the run's timestamps. Those arrive browser-local
    /// (<c>UtcToLocalDateTimeConverter</c>); subtracting them from a UTC value measured the browser's
    /// time-zone offset as elapsed time, so in UTC+2 a running stage read zero for two hours.
    /// </summary>
    internal static DateTime NowFor(TimeProvider? clock, DateTime reference)
    {
        var utc = clock is not null ? clock.GetUtcNow().UtcDateTime : ServerClock.UtcNow;
        return reference.Kind == DateTimeKind.Local ? utc.ToLocalTime() : utc;
    }

    /// <summary>The elapsed time at 0, 25, 50, 75 and 100 % of the axis.</summary>
    internal static IReadOnlyList<Tick> Ticks(RunGanttLayout layout) =>
        [.. new[] { 0, 25, 50, 75, 100 }.Select(percent =>
            new Tick(percent, FormatDuration(TimeSpan.FromTicks(layout.Duration.Ticks * percent / 100))))];

    /// <summary>The run's own duration, measured like the Started tile (run start to run end, or to
    /// now while it runs). Null when the run carries no start.</summary>
    internal TimeSpan? RunDuration()
    {
        if (Run is null || Run.StartedAt == default) return null;
        var end = Run.CompletedAt ?? NowFor(Clock, Run.StartedAt);
        var span = end - Run.StartedAt;
        return span > TimeSpan.Zero ? span : TimeSpan.Zero;
    }

    /// <summary>The status as an icon beside the name, so it does not rest on the bar's hue
    /// alone.</summary>
    private static OmniIconName StatusIcon(TaskExecutionStatus status) => status switch
    {
        TaskExecutionStatus.Success => OmniIconName.CheckCircle,
        TaskExecutionStatus.Failed => OmniIconName.Error,
        TaskExecutionStatus.Cancelled => OmniIconName.Prohibit,
        TaskExecutionStatus.Running => OmniIconName.Play,
        _ => OmniIconName.Hourglass
    };

    private static string StatusIconClass(StageViewModel stage) => IconClassOf(StatusClass(stage));

    private static string StatusIconClass(PipelineStepRunDto step) => IconClassOf(StatusClass(step));

    private static string IconClassOf(string statusClass) =>
        "run-gantt-status-icon run-gantt-status-" + statusClass["run-gantt-".Length..];

    private string StatusText(TaskExecutionStatus status) => L.Localize(status);

    /// <summary>Steps stored as Cancelled because their condition was false display the skipped
    /// icon and label.</summary>
    private static OmniIconName StageIcon(StageViewModel stage) =>
        stage.SkippedCondition is { Length: > 0 } ? PipelineRunFormatting.SkippedIcon
        : stage.IsRejected ? OmniIconName.Warning
        : StatusIcon(stage.Status);

    private string StageStatusText(StageViewModel stage) =>
        stage.SkippedCondition is { Length: > 0 } ? L["Skipped"]
        : stage.IsRejected ? L.Localize(PipelineStatus.Rejected)
        : StatusText(stage.Status);

    private static OmniIconName StepIcon(PipelineStepRunDto step) =>
        PipelineRunFormatting.IsSkippedByCondition(step) ? PipelineRunFormatting.SkippedIcon
        : step.IsRejected ? OmniIconName.Warning
        : StatusIcon(step.Status);

    private string StepStatusText(PipelineStepRunDto step) =>
        PipelineRunFormatting.IsSkippedByCondition(step) ? L["Skipped"]
        : step.IsRejected ? L.Localize(PipelineStatus.Rejected)
        : StatusText(step.Status);

    private static string SignedDuration(TimeSpan span) =>
        (span < TimeSpan.Zero ? "-" : "+") + FormatDuration(span.Duration());

    private static string Seconds(double? seconds) =>
        seconds is { } value ? FormatDuration(TimeSpan.FromSeconds(value)) : string.Empty;

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h{span.Minutes:D2}"
            : span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}m{span.Seconds:D2}"
                // A negative span cannot come from the layout (ends are clamped), but the floor keeps
                // a "-0s" out of the UI if that ever changes.
                : $"{Math.Max(0, (int)span.TotalSeconds)}s";
}
