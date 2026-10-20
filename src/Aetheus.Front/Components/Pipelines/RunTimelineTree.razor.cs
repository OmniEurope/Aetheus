// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recursive, collapsible timeline for one pipeline run. Renders the run's own stages/steps; a
/// <c>type: trigger</c> step (<see cref="PipelineStepRunDto.TriggeredRunId"/>) becomes a collapsible child
/// node that lazy-loads and renders the triggered run's own timeline on expand, keyed by run id, so the
/// same pipeline triggered twice yields two distinct nodes. Selecting a real step bubbles up via
/// <see cref="OnSelectStep"/> so the parent page's shared log pane shows its logs.
/// </summary>
public partial class RunTimelineTree
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IUserPreferences Preferences { get; set; } = default!;
    // Keep child navigation as a native link: it remains usable even while Blazor is reconnecting.
    // stopPropagation in the markup prevents the containing expandable row from consuming the click.
    private string ChildRunHref(int childRunId) => PipelineRunPresentation.Href(childRunId, ProjectId, ServerId);

    [Parameter, EditorRequired] public PipelineRunDto Run { get; set; } = default!;
    [Parameter] public int Depth { get; set; }
    [Parameter] public int? SelectedStepId { get; set; }
    [Parameter] public EventCallback<PipelineStepRunDto> OnSelectStep { get; set; }
    [Parameter] public int? ProjectId { get; set; }
    [Parameter] public int? ServerId { get; set; }
    // Child runs already fetched by the parent page (keyed by run id): seeds this tree so expansion is
    // instant and the branch containing SelectedStepId can be auto-revealed without a per-click fetch.
    [Parameter] public IReadOnlyDictionary<int, PipelineRunDto>? PreloadedChildren { get; set; }
    // Usual durations per run id (this run and its children), from the page loader.
    [Parameter] public IReadOnlyDictionary<int, RunStageBaselinesDto>? Baselines { get; set; }

    // Belt-and-braces bound on recursion + the per-child fetch (the trigger cycle-guard already prevents loops).
    private const int MaxDepth = 6;

    private List<StageViewModel> _stages = [];
    private readonly HashSet<int> _expanded = [];
    private readonly Dictionary<int, PipelineRunDto?> _childRuns = [];
    private readonly HashSet<int> _loading = [];
    private bool _restored;

    // S-FEAT-EXST: which child-run nodes the user had open, persisted per run id so a SignalR reload or
    // a navigation away-and-back doesn't re-collapse the tree the user expanded during a live run.
    private RunTimelineExpansionStore? _expansionStore;
    private RunTimelineExpansionStore ExpansionStore => _expansionStore ??= new(Preferences);

    // Once the selected step's branch has been auto-expanded, don't fight the user re-collapsing it.
    private int? _autoExpandedFor;

    /// <summary>Stages that share their `depends_on` depth with at least one other stage, and
    /// therefore ran at the same time. Without this the page reads as a sequence it never was: a
    /// stage further down turns green while one above it is still going, and nothing says why.</summary>
    private HashSet<string> _concurrentStages = new(StringComparer.OrdinalIgnoreCase);

    private bool RunsConcurrently(StageViewModel stage) => _concurrentStages.Contains(stage.Name);

    protected override void OnParametersSet()
    {
        _stages = RunStageBuilder.Build(Run);
        _concurrentStages = [.. _stages
            .Where(stage => stage.Depth is not null)
            .GroupBy(stage => stage.Depth!.Value)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(stage => stage.Name)];
        if (PreloadedChildren is not null)
            foreach (var kv in PreloadedChildren)
                _childRuns[kv.Key] = kv.Value;
        AutoExpandToSelected();
    }

    // Reveal the currently-selected step by expanding the trigger branch that contains it, so navigating
    // from the Overview to the Logs opens the timeline on the right node (not a collapsed child).
    private void AutoExpandToSelected()
    {
        if (SelectedStepId is not int sel || _autoExpandedFor == sel) return;
        _autoExpandedFor = sel;
        foreach (var stage in _stages)
            foreach (var step in stage.Steps)
                if (step.TriggeredRunId is int childId && _childRuns.TryGetValue(childId, out var child) && child is not null && ContainsStep(child, sel, []))
                    _expanded.Add(childId);
    }

    private bool ContainsStep(PipelineRunDto run, int stepId, HashSet<int> visitedRunIds)
    {
        if (!visitedRunIds.Add(run.Id)) return false;
        if (run.Steps.Any(s => s.Id == stepId)) return true;
        foreach (var s in run.Steps)
            if (s.TriggeredRunId is int cid && _childRuns.TryGetValue(cid, out var c) && c is not null && ContainsStep(c, stepId, visitedRunIds))
                return true;
        return false;
    }

    // Trigger step names follow the "Description (child-pipeline)" convention (e.g. "Build & test (toto-ci)").
    // Split them so the row renders the child pipeline as a chip and the description as the label, matching
    // the run-timeline mockup. Degrades gracefully to (whole name, null) when there is no trailing "(...)".
    private static (string Desc, string? Chip) SplitTriggerName(string stepName)
    {
        var open = stepName.LastIndexOf('(');
        if (open > 0 && stepName.EndsWith(')'))
        {
            var chip = stepName[(open + 1)..^1].Trim();
            var desc = stepName[..open].Trim();
            if (chip.Length > 0 && desc.Length > 0)
                return (desc, chip);
        }
        return (stepName, null);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _restored) return;
        _restored = true;

        var saved = await ExpansionStore.LoadAsync(Run.Id);
        if (saved is null || saved.Length == 0) return;

        // Only re-open ids that are genuinely trigger children of THIS run (guards stale/foreign ids).
        var validChildIds = _stages.SelectMany(s => s.Steps)
            .Select(st => st.TriggeredRunId).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        foreach (var childId in saved)
            if (validChildIds.Contains(childId) && !_expanded.Contains(childId))
                await ToggleChild(childId);   // expand + lazy-fetch, exactly as a user click would

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// What a triggered child is doing right now, so a collapsed trigger row says more than "running".
    /// Descends through the child's own trigger steps: on a candidate the parent's row is the only one
    /// visible, and the stage actually executing is two or three levels below it.
    /// </summary>
    internal string? CurrentChildStage(int childRunId) => CurrentStage(childRunId, []);

    private string? CurrentStage(int runId, HashSet<int> visited)
    {
        if (!visited.Add(runId) || ChildRun(runId) is not { } run) return null;

        foreach (var step in run.Steps.Where(step => step.Status == TaskExecutionStatus.Running))
        {
            // A running trigger step is not doing the work: its own child is.
            if (step.TriggeredRunId is int grandChildId)
                return CurrentStage(grandChildId, visited) ?? StageLabel(run, step);
            return StageLabel(run, step);
        }
        return null;
    }

    /// <summary>The child's pipeline and stage, because a bare stage name loses which run it is in
    /// once the chain is more than one level deep.</summary>
    private static string StageLabel(PipelineRunDto run, PipelineStepRunDto step) =>
        $"{run.PipelineName} · {step.StageName}";

    private bool IsExpanded(int childRunId) => _expanded.Contains(childRunId);
    private bool IsLoading(int childRunId) => _loading.Contains(childRunId);
    private PipelineRunDto? ChildRun(int childRunId) => _childRuns.GetValueOrDefault(childRunId);

    /// <summary>Running steps show their average duration from successful runs; resource
    /// measurements belong in the aligned progression columns.</summary>
    private string? UsualDuration(PipelineStepRunDto step)
    {
        if (step.Status != TaskExecutionStatus.Running || step.TriggeredRunId is not null) return null;
        if (Baselines?.GetValueOrDefault(Run.Id) is not { } baselines) return null;

        var usual = baselines.Steps.FirstOrDefault(candidate =>
            string.Equals(candidate.StageName, step.StageName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.StepName, step.StepName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeMatrixLeg(candidate.MatrixLeg), NormalizeMatrixLeg(step.MatrixLeg),
                StringComparison.OrdinalIgnoreCase)
            && candidate.IsSystem == step.IsSystem);
        return usual is { AverageSeconds: > 0 }
            ? PipelineRunFormatting.FormatDuration(DateTime.UnixEpoch, DateTime.UnixEpoch + TimeSpan.FromSeconds(usual.AverageSeconds))
            : null;
    }

    private static string NormalizeMatrixLeg(string? matrixLeg) => matrixLeg?.Trim() ?? string.Empty;

    private async Task ToggleChild(int childRunId)
    {
        // Add returns false when it was already present → this click collapses it.
        if (!_expanded.Add(childRunId)) { _expanded.Remove(childRunId); await PersistExpandedAsync(); return; }
        await PersistExpandedAsync();
        if (Depth >= MaxDepth) return;
        // Re-fetch on expand unless we already hold a *terminal* snapshot: a still-running child keeps
        // progressing after the first load, so its cached snapshot goes stale (and a prior failed fetch
        // cached null deserves a retry). Only a finished child is safe to keep.
        if (_childRuns.TryGetValue(childRunId, out var cached)
            && cached is not null && PipelineRunFormatting.IsTerminal(cached.Status)) return;

        _loading.Add(childRunId);
        // Returns null on transport or payload errors so the timeline shows child-run
        // unavailability without crashing.
        try { _childRuns[childRunId] = await Api.Pipelines.GetPipelineRunAsync(childRunId); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { _childRuns[childRunId] = null; }
        finally { _loading.Remove(childRunId); }
    }

    private Task PersistExpandedAsync() => ExpansionStore.SaveAsync(Run.Id, _expanded);
}
