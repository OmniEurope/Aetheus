// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Components.Pipelines;

public partial class VisualPipelineEditor : IAsyncDisposable
{
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private YamlSerializationService YamlService { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private ILogger<VisualPipelineEditor> Logger { get; set; } = default!;

    [Parameter] public string YamlDefinition { get; set; } = string.Empty;
    [Parameter] public EventCallback<string> YamlDefinitionChanged { get; set; }
    [Parameter] public List<string> AvailableServers { get; set; } = [];
    [Parameter] public List<string> AvailableLibraries { get; set; } = [];
    [Parameter] public List<string> AvailableVaults { get; set; } = [];
    // S-FEAT-VPNP: persist manually-dragged node positions per pipeline. Null (new pipeline) = no persistence.
    [Parameter] public int? PipelineId { get; set; }
    [Parameter] public string? BaseTemplateYaml { get; set; }

    private readonly string _canvasId = $"vp-canvas-{Guid.NewGuid():N}";
    private DotNetObjectReference<VisualPipelineEditor>? _dotNetRef;
    private bool _initialized;
    private bool _fittedOnce;
    private bool _needsPostLayout;
    private bool _positionsLoaded;

    private PipelineYamlDefinition _definition = new();
    private PipelineYamlDefinition? _baseDefinition;
    private string? _lastBaseTemplateYaml;
    private int? _lastPipelineId;
    private Dictionary<string, NodePosition> _nodePositions = [];
    private List<EdgeData> _edges = [];
    private string? _selectedStageId;
    private List<PipelineStageDefinition> DisplayStages => _baseDefinition is null
        ? _definition.Stages
        : PipelineVisualInheritanceHelper.MergeStages(_baseDefinition.Stages, _definition.Stages);
    private PipelineYamlDefinition EffectiveDefinition => _definition with { Stages = DisplayStages };
    private int _selectedStageIndex => _selectedStageId is not null
        && int.TryParse(_selectedStageId, out var displayIndex)
        ? GetLocalStageIndex(displayIndex)
        : -1;
    private bool _parseError;
    private string? _lastParsedYaml;
    private string? _lastRenderedYaml;
    private bool _settingsExpanded;

    private string _newPipelineVarKey = string.Empty;
    private string _newPipelineVarValue = string.Empty;

    private readonly UndoRedoStack<PipelineYamlDefinition> _history = new();
    private bool CanUndo => _history.CanUndo;
    private bool CanRedo => _history.CanRedo;

    private static readonly List<string> _triggerOptions = ["manual", "webhook", "schedule"];

    private sealed record StageTemplateItem(string Text, int Value);
    private List<PipelineTemplateSummaryDto> _stageTemplates = [];
    private List<StageTemplateItem> _stageTemplateItems = [];
    private int? _selectedStageTemplateId;

    private async Task OnStageTemplateChanged(int? templateId)
    {
        _selectedStageTemplateId = templateId;
        if (templateId.HasValue)
        {
            await OnInsertStageTemplate(templateId.Value);
            _selectedStageTemplateId = null;
        }
    }

    protected override void OnParametersSet()
    {
        if (_lastPipelineId != PipelineId)
        {
            _lastPipelineId = PipelineId;
            _positionsLoaded = false;
            _fittedOnce = false;
            _nodePositions.Clear();
            _nodeHeights.Clear();
        }
        if (YamlDefinition == _lastParsedYaml && BaseTemplateYaml == _lastBaseTemplateYaml) return;

        var parsed = YamlService.Parse(YamlDefinition);
        if (parsed is not null)
        {
            _definition = parsed;
            _baseDefinition = string.IsNullOrWhiteSpace(BaseTemplateYaml) ? null : YamlService.Parse(BaseTemplateYaml);
            _parseError = false;
            _lastParsedYaml = YamlDefinition;
            _lastBaseTemplateYaml = BaseTemplateYaml;
            _lastRenderedYaml = null;
            _positionsLoaded = false;
            _nodePositions.Clear();
        }
        else if (!string.IsNullOrWhiteSpace(YamlDefinition))
        {
            _parseError = true;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("visualPipeline.initCanvas", _canvasId, _dotNetRef, true);
            _initialized = true;
            await LoadStageTemplatesAsync();
            StateHasChanged();
        }

        if (_initialized && !_parseError && DisplayStages.Count > 0)
        {
            var currentYaml = _lastParsedYaml;
            if (currentYaml != _lastRenderedYaml)
            {
                _lastRenderedYaml = currentYaml;
                await ComputeLayoutAsync();
                await SetupDragHandlersAsync();
                _needsPostLayout = true;
                // Re-render so the freshly computed node positions/edges reach the DOM.
                // Guard already flipped (_lastRenderedYaml == currentYaml), so this won't re-loop.
                StateHasChanged();
            }
            else if (_needsPostLayout)
            {
                _needsPostLayout = false;
                // Nodes are now in the DOM: S-TECH-VPNH measure real heights and re-anchor edges;
                // S-UX-VPLE hand the edge topology to JS for live redraw during a drag; S-UX-VPFV fit the
                // view once so every node is visible without manual zoom.
                _nodeHeights = await Js.InvokeAsync<Dictionary<string, double>>("visualPipeline.getNodeHeights", _canvasId) ?? [];
                RecomputeEdges();
                await Js.InvokeVoidAsync("visualPipeline.setEdges", _canvasId, BuildEdgeTopology());
                if (!_fittedOnce) { _fittedOnce = true; await Js.InvokeVoidAsync("visualPipeline.fitView", _canvasId); }
                StateHasChanged();
            }
        }
    }

    // S-UX-VPLE: edge topology (source/target node indices) so JS can redraw the connected paths live while
    // a node is dragged, instead of waiting for the pointerup round-trip to .NET.
    private object BuildEdgeTopology() =>
        _edges.Select(e => new { from = e.From, to = e.To }).ToArray();

    private async Task ComputeLayoutAsync()
    {
        var stages = DisplayStages.Select((s, i) => new
        {
            id = i.ToString(),
            name = s.Name,
            dependsOn = s.DependsOn,
            stepCount = s.Steps.Count
        }).ToArray();

        var result = await Js.InvokeAsync<LayoutResult>("visualPipeline.computeLayout", (object)stages);
        if (result is null) return;

        var layoutNodeIds = new HashSet<string>(result.Nodes.Count);
        foreach (var node in result.Nodes)
        {
            layoutNodeIds.Add(node.Id);
            // Only set position for nodes that weren't manually moved
            _nodePositions.TryAdd(node.Id, new NodePosition(node.X, node.Y));
        }

        // Remove positions for nodes that no longer exist
        foreach (var key in _nodePositions.Keys.Except(layoutNodeIds).ToList())
            _nodePositions.Remove(key);

        await LoadPersistedPositionsAsync();
        RecomputeEdges();
    }

    // S-FEAT-VPNP: overlay any manually-saved node positions for this pipeline over the dagre layout, so a
    // hand-arranged graph survives a reload. Keyed by node index (matches the in-memory model, which is
    // cleared on structural edits). No-op for a new (unsaved) pipeline or when nothing was saved.
    // Ordered stage names: the layout store keys saved positions on this so any structural edit
    // (add/remove/reorder) invalidates a stale index-keyed layout instead of mis-placing nodes. Length-
    // prefixed (not a `|`-join) so a stage literally named "a|b" can't collide with two stages "a","b".
    private string StructureSignature() =>
        string.Concat(DisplayStages.Select(s => s.Name.Length.ToString() + ":" + s.Name));

    private VisualPipelineLayoutStore LayoutStore => _layoutStore ??= new VisualPipelineLayoutStore(Js);
    private VisualPipelineLayoutStore? _layoutStore;

    private async Task LoadPersistedPositionsAsync()
    {
        if (_positionsLoaded) return;
        if (PipelineId is { } id) await LayoutStore.LoadIntoAsync(id, StructureSignature(), _nodePositions);
        _positionsLoaded = true;
    }

    private Task SavePersistedPositionsAsync() =>
        PipelineId is { } id ? LayoutStore.SaveAsync(id, StructureSignature(), _nodePositions) : Task.CompletedTask;

    // S-TECH-VPNH: real measured node heights (offsetHeight), keyed by node id, captured after layout so
    // edges anchor to what is actually rendered rather than a formula that can drift from the CSS.
    private Dictionary<string, double> _nodeHeights = [];

    private double MeasuredHeight(string nodeId, PipelineStageDefinition stage) =>
        _nodeHeights.TryGetValue(nodeId, out var h) && h > 0 ? h : VisualPipelineGeometry.NodeHeight(stage.Steps.Count);

    // Rebuild edge paths from the CURRENT node positions so connectors follow nodes after a drag.
    // Anchored right-middle of source -> left-middle of target (matches the LR pipeline flow).
    private void RecomputeEdges()
    {
        var stages = DisplayStages;
        var edges = new List<EdgeData>();
        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            var toId = i.ToString();
            if (!_nodePositions.TryGetValue(toId, out var toPos)) continue;
            foreach (var dep in stage.DependsOn)
            {
                var depIndex = stages.FindIndex(s => s.Name == dep);
                if (depIndex < 0) continue;
                var fromId = depIndex.ToString();
                if (!_nodePositions.TryGetValue(fromId, out var fromPos)) continue;

                var y1 = fromPos.Y + MeasuredHeight(fromId, stages[depIndex]) / 2;
                var y2 = toPos.Y + MeasuredHeight(toId, stage) / 2;
                edges.Add(new EdgeData(fromId, toId,
                    VisualPipelineGeometry.BuildEdgePath(fromPos.X + VisualPipelineGeometry.NodeWidth, y1, toPos.X, y2)));
            }
        }
        _edges = edges;
    }

    private async Task SetupDragHandlersAsync()
    {
        for (var i = 0; i < DisplayStages.Count; i++)
        {
            var nodeId = $"vp-node-{_canvasId}-{i}";
            await Js.InvokeVoidAsync("visualPipeline.enableDrag", nodeId, i.ToString(), _dotNetRef);
        }
    }

    [JSInvokable]
    public async Task OnNodeMoved(string stageId, double x, double y)
    {
        _nodePositions[stageId] = new NodePosition(x, y);
        RecomputeEdges();
        await SavePersistedPositionsAsync();
        StateHasChanged();
    }

    [JSInvokable]
    public void OnCanvasClicked()
    {
        _selectedStageId = null;
        StateHasChanged();
    }

    private void PushUndo()
    {
        _history.Push(_definition);
    }

    private async Task OnUndo()
    {
        var state = _history.Undo(_definition);
        if (state is null) return;
        _definition = state;
        _nodePositions.Clear();
        await EmitYamlChange();
        await ComputeLayoutAsync();
    }

    private async Task OnRedo()
    {
        var state = _history.Redo(_definition);
        if (state is null) return;
        _definition = state;
        _nodePositions.Clear();
        await EmitYamlChange();
        await ComputeLayoutAsync();
    }

    private async Task EmitYamlChange()
    {
        var yaml = YamlService.Serialize(_definition);
        _lastParsedYaml = yaml;
        await YamlDefinitionChanged.InvokeAsync(yaml);
    }

    // --- Stage operations ---

    private async Task OnCanvasKeyDown(KeyboardEventArgs e)
    {
        if (e.CtrlKey && e.Key == "z") { await OnUndo(); return; }
        if (e.CtrlKey && e.Key == "y") { await OnRedo(); return; }
        if (e.Key is "Delete" or "Backspace" && _selectedStageId is not null)
            await OnDeleteStage(_selectedStageId);
    }

    private async Task OnStageSelected(string stageId)
    {
        _selectedStageId = _selectedStageId == stageId ? null : stageId;
        // S-DES-VPSC: when a node becomes selected, scroll/pan the canvas so it is centred and visible.
        if (_selectedStageId is not null)
            await Js.InvokeVoidAsync("visualPipeline.centerNode", _canvasId, $"vp-node-{_canvasId}-{stageId}");
    }

    // The stage-template picker is an optional aid (SITE-FUNC-AUTOCOMPLETE-001): a network failure
    // leaves it empty and is logged, it never escapes OnAfterRenderAsync and replaces the editor.
    private async Task LoadStageTemplatesAsync()
    {
        try
        {
            _stageTemplates = await Api.PipelineTemplates.GetPipelineTemplatesAsync();
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "[VisualPipelineEditor] Stage templates could not be loaded");
            _stageTemplates = [];
        }
        _stageTemplateItems = _stageTemplates
            .Select((t, i) => new StageTemplateItem(t.Name, i))
            .ToList();
    }

    private async Task OnAddStage()
    {
        var stages = _definition.Stages.ToList();
        var name = $"stage-{stages.Count + 1}";
        stages.Add(new PipelineStageDefinition
        {
            Name = name,
            Agent = "default",
            Steps = []
        });
        PushUndo();
        _definition = _definition with { Stages = stages };
        _nodePositions.Clear();
        await EmitYamlChange();
    }

    private async Task OnInsertStageTemplate(object value)
    {
        if (value is not int idx || idx < 0 || idx >= _stageTemplates.Count) return;
        var summary = _stageTemplates[idx];
        var full = await Api.PipelineTemplates.GetPipelineTemplateAsync(summary.Id);
        if (full is null) return;
        var parsed = YamlService.Parse(full.YamlContent);
        if (parsed is null || parsed.Stages.Count == 0) return;

        var stages = PipelineStageTemplateMerger.Append(_definition.Stages, parsed.Stages);
        PushUndo();
        _definition = _definition with { Stages = stages };
        _nodePositions.Clear();
        await EmitYamlChange();
    }

    private async Task OnDeleteStage(string stageId)
    {
        if (!int.TryParse(stageId, out var displayIndex)) return;
        var index = GetLocalStageIndex(displayIndex);
        if (index < 0) return;

        var stageName = _definition.Stages[index].Name;
        var confirmed = await Dialog.Confirm(
            L["DeleteStageConfirm"].Value,
            L["DeleteStage"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var stages = _definition.Stages.ToList();
        stages.RemoveAt(index);

        // Clean up DependsOn references
        for (var i = 0; i < stages.Count; i++)
        {
            if (stages[i].DependsOn.Contains(stageName))
            {
                var deps = stages[i].DependsOn.Where(d => d != stageName).ToList();
                stages[i] = stages[i] with { DependsOn = deps };
            }
        }

        PushUndo();
        _definition = _definition with { Stages = stages };
        _selectedStageId = null;
        _nodePositions.Clear();
        await EmitYamlChange();
    }

    private async Task OnDuplicateStage(string stageId)
    {
        if (!int.TryParse(stageId, out var displayIndex)) return;
        var index = GetLocalStageIndex(displayIndex);
        if (index < 0) return;

        var source = _definition.Stages[index];
        var stages = _definition.Stages.ToList();

        var baseName = source.Name;
        var name = $"{baseName}-copy";
        var counter = 2;
        while (stages.Any(s => s.Name == name)) { name = $"{baseName}-copy-{counter++}"; }

        var clone = source with
        {
            Name = name,
            Steps = source.Steps.Select(s => s with { }).ToList()
        };
        stages.Insert(index + 1, clone);
        PushUndo();
        _definition = _definition with { Stages = stages };
        _nodePositions.Clear();
        await EmitYamlChange();
    }

    private async Task OnSelectedStageChanged(PipelineStageDefinition updated)
    {
        if (_selectedStageIndex < 0 || _selectedStageIndex >= _definition.Stages.Count) return;

        PushUndo();
        var stages = _definition.Stages.ToList();
        stages[_selectedStageIndex] = updated;
        _definition = _definition with { Stages = stages };
        await EmitYamlChange();
    }

    private List<string> GetOtherStageNames(int excludeIndex)
    {
        return _definition.Stages
            .Where((_, i) => i != excludeIndex)
            .Select(s => s.Name)
            .ToList();
    }

    // --- Step operations ---

    private async Task OnAddStepToStage(string stageId)
    {
        if (!int.TryParse(stageId, out var displayIndex)) return;
        var index = GetLocalStageIndex(displayIndex);
        if (index < 0) return;

        var result = await Dialog.OpenAsync<StepEditDialog>(
            L["AddStep"].Value,
            new Dictionary<string, object?> { { "Step", null } },
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });

        if (result is PipelineStepDefinition newStep)
        {
            PushUndo();
            var stages = _definition.Stages.ToList();
            var steps = stages[index].Steps.ToList();
            steps.Add(newStep);
            stages[index] = stages[index] with { Steps = steps };
            _definition = _definition with { Stages = stages };
            await EmitYamlChange();
        }
    }

    private async Task OnEditStepInStage(string stageId, int stepIndex)
    {
        if (!int.TryParse(stageId, out var displayIndex)) return;
        var stageIdx = GetLocalStageIndex(displayIndex);
        if (stageIdx < 0) return;
        var stage = _definition.Stages[stageIdx];
        if (stepIndex < 0 || stepIndex >= stage.Steps.Count) return;

        var result = await Dialog.OpenAsync<StepEditDialog>(
            L["EditStep"].Value,
            new Dictionary<string, object?> { { "Step", stage.Steps[stepIndex] } },
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });

        if (result is PipelineStepDefinition updated)
        {
            PushUndo();
            var stages = _definition.Stages.ToList();
            var steps = stages[stageIdx].Steps.ToList();
            steps[stepIndex] = updated;
            stages[stageIdx] = stages[stageIdx] with { Steps = steps };
            _definition = _definition with { Stages = stages };
            await EmitYamlChange();
        }
    }

    // --- Pipeline-level settings ---

    private async Task OnTriggerChanged(string value)
    {
        PushUndo();
        _definition = _definition with { Trigger = value };
        await EmitYamlChange();
    }

    private async Task OnLibrariesChanged(IEnumerable<string>? values)
    {
        PushUndo();
        _definition = _definition with { VariableLibraries = values?.ToList() ?? [] };
        await EmitYamlChange();
    }

    private async Task OnVaultsChanged(IEnumerable<string>? values)
    {
        PushUndo();
        _definition = _definition with { Vaults = values?.ToList() ?? [] };
        await EmitYamlChange();
    }

    private async Task OnPipelineVarChanged(string key, string value)
    {
        PushUndo();
        var vars = new Dictionary<string, string>(_definition.Variables) { [key] = value };
        _definition = _definition with { Variables = vars };
        await EmitYamlChange();
    }

    private async Task OnRemovePipelineVar(string key)
    {
        PushUndo();
        var vars = new Dictionary<string, string>(_definition.Variables);
        vars.Remove(key);
        _definition = _definition with { Variables = vars };
        await EmitYamlChange();
    }

    private async Task OnAddPipelineVar()
    {
        if (string.IsNullOrWhiteSpace(_newPipelineVarKey)) return;
        PushUndo();
        var vars = new Dictionary<string, string>(_definition.Variables) { [_newPipelineVarKey] = _newPipelineVarValue };
        _definition = _definition with { Variables = vars };
        _newPipelineVarKey = string.Empty;
        _newPipelineVarValue = string.Empty;
        await EmitYamlChange();
    }

    private List<string> GetStageWarnings(PipelineStageDefinition stage) =>
        new PipelineVisualValidator(L).GetStageWarnings(stage, EffectiveDefinition);

    private string StageProvenance(PipelineStageDefinition stage) => ProvenanceText(
        PipelineVisualInheritanceHelper.GetStageProvenance(stage, _baseDefinition, _definition));

    private int GetLocalStageIndex(int displayIndex)
    {
        var stages = DisplayStages;
        if (displayIndex < 0 || displayIndex >= stages.Count) return -1;
        var name = stages[displayIndex].Name;
        return _definition.Stages.FindIndex(stage => !stage.Remove &&
            string.Equals(stage.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<PipelineProvenanceItem> ProvenanceItems =>
        _baseDefinition is null ? [] : PipelineProvenanceHelper.Analyze(_definition, _baseDefinition);

    private string ProvenanceText(PipelineElementProvenance provenance) => provenance switch
    {
        PipelineElementProvenance.Inherited => L["Inherited"],
        PipelineElementProvenance.Overridden => L["Overridden"],
        _ => L["Local"]
    };

    private static OmniTone ProvenanceStyle(PipelineElementProvenance provenance) => provenance switch
    {
        PipelineElementProvenance.Inherited => OmniTone.Neutral,
        PipelineElementProvenance.Overridden => OmniTone.Warning,
        _ => OmniTone.Accent
    };

    // --- Zoom / Layout ---

    private async Task OnAutoLayout()
    {
        _nodePositions.Clear();
        await ComputeLayoutAsync();
    }

    private async Task OnZoomIn()
    {
        var current = await Js.InvokeAsync<double>("visualPipeline.getScale", _canvasId);
        await Js.InvokeVoidAsync("visualPipeline.zoomTo", _canvasId, current + 0.2);
    }

    private async Task OnZoomOut()
    {
        var current = await Js.InvokeAsync<double>("visualPipeline.getScale", _canvasId);
        await Js.InvokeVoidAsync("visualPipeline.zoomTo", _canvasId, current - 0.2);
    }

    private async Task OnFitView()
    {
        await Js.InvokeVoidAsync("visualPipeline.fitView", _canvasId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_initialized)
        {
            try
            {
                await Js.InvokeVoidAsync("visualPipeline.dispose", _canvasId);
            }
            catch (JSDisconnectedException) { /* Circuit disconnected */ }
            catch (JSException) { /* Circuit disconnected */ }
        }
        _dotNetRef?.Dispose();
    }
}
