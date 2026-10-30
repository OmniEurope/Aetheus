// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using static Aetheus.Front.Components.Shared.PipelineRunFormatting;

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRun : IAsyncDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private PageStateCache PageState { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ILogger<PipelineRun> Logger { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private PendingApprovalsService PendingApprovals { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Parameter] public int RunId { get; set; }
    [SupplyParameterFromQuery] public int? ProjectId { get; set; }
    [SupplyParameterFromQuery] public int? ServerId { get; set; }
    /// <summary>Narrowest an overview tile may be (recette R-15): the grid shows four tiles per row while each stays at
    /// least this wide, and three, two or one when the area, sidebar included, leaves less. A CSS length, OE's
    /// <c>OmniTileGrid.MinColumnWidth</c>.</summary>
    internal const string RunTileMinColumnWidth = "14rem";

    private int? EffectiveProjectId => _run?.ProjectId ?? ProjectId;

    private string PipelineHref => EffectiveProjectId is { } projectId
        ? $"/pipelines/{_run!.PipelineId}?projectId={projectId}"
        : ServerId is { } serverId ? $"/pipelines/{_run!.PipelineId}?serverId={serverId}" : $"/pipelines/{_run!.PipelineId}";
    private PipelineRunDto? _run;
    private PipelineRunsSummary? _pipelineRunsSummary;
    private PipelineRunMetrics _metrics = new();
    private readonly PipelineRunGateState _gateState = new();
    private readonly PipelineRunSchemaState _schemaState = new();
    private readonly PipelineRunPageCache _pageCache = new();
    private List<ReleaseDto> _releases = [];
    private List<AiRunResultDto> _aiResults = [];
    // Internal mirror id used by the Repository tile instead of the raw smart-HTTP URL.
    private int? _repoId;
    private List<StageViewModel> _stages = [];
    private bool _loading;
    // The run is shown while the rest of the page still loads behind it.
    private bool _detailsLoading;
    private bool _childrenLoaded;
    [SupplyParameterFromQuery(Name = "tab")] public string? Tab { get; set; }
    /// <summary>A step a link asks to open in the logs (the Gate tab's "the F comes from here").</summary>
    [SupplyParameterFromQuery(Name = "step")] public int? StepId { get; set; }
    private int? _appliedStepId;
    private int _selectedTab;
    private readonly HashSet<string> _expandedMatrixGroups = [];
    // Owns the pipelines-hub connection + debounced reload + child-group joining (extracted collaborator).
    private PipelineRunLiveConnection? _live;
    private bool _rerunning;
    // F2: the run's single Pending approval (if any) and its decision form state.
    private PipelineRunApprovalController? _approval;
    private PipelineRunRerunCoordinator? _rerunCoordinator;
    private PipelineRunCommandCoordinator? _commandCoordinator;
    private bool _retrying;
    // S-TECH-32: live log streaming for the selected running step (poll stays as fallback). Owned by
    // the log streamer collaborator; the component keeps the shared log cache it writes into.
    private PipelineRunLogStreamer? _logStreamer;
    private PipelineRunStageBaselines _baselines = default!;
    private int? _loadedRunId;
    private readonly PipelineRunLoadSession _loadSession = new();

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedRunId == RunId)
        {
            // Same run, another ?step=: a link from this run's own Gate tab to one of its steps.
            if (StepId == _appliedStepId || _detailsLoading) return;
            _appliedStepId = StepId;
            if (RequestedStep() is { } requested) await SelectStep(requested);
            return;
        }

        _loadedRunId = RunId;
        var load = _loadSession.Begin();
        try { await LoadRunPageAsync(RunId, load.Generation, load.Token); }
        catch (OperationCanceledException) when (load.Token.IsCancellationRequested) { }
    }

    private async Task LoadRunPageAsync(int runId, int generation, CancellationToken ct)
    {
        if (!await ResetRunPageAsync(runId, generation).ConfigureAwait(false)) return;
        _logStreamer = new PipelineRunLogStreamer(Api, HubFactory, Logger, _logWindow, InvokeAsync, StateHasChanged);
        _baselines = new PipelineRunStageBaselines(Api);
        _approval = new PipelineRunApprovalController(Api, Toast, ReloadRunAsync, PendingApprovals);
        // A return shows the run as it was left at once; the page-wide loader is for a first visit.
        // The run is then read again and the page rebuilt from it, as on a first visit.
        var address = $"pipelines/runs/{runId}";
        var ticket = PageState.Begin();
        _loading = !PageState.TryRestore<PipelineRunDto>(address, out var shown);
        if (!_loading)
        {
            _run = shown;
            _detailsLoading = true;
            PrepareRootRun();
            await InvokeAsync(StateHasChanged);
        }
        _run = await LoadRootRunAsync(runId, refresh: !_loading, ct).ConfigureAwait(false);
        var approvalLoaded = await _approval.LoadIfWaitingAsync(_run, ct).ConfigureAwait(false);
        if (!IsCurrentLoad(runId, generation)) return;
        _loading = false;
        if (_run is null) PageState.Forget<PipelineRunDto>(address);
        else
        {
            // The run loads first so the header can render while child runs, gates, metrics,
            // approvals, releases and logs load in parallel.
            _detailsLoading = true;
            PrepareRootRun();
            PageState.Remember(ticket, address, _run);
            await InvokeAsync(StateHasChanged);
            if (!await EnrichRunAsync(runId, generation, approvalLoaded, ct).ConfigureAwait(false)) return;
            _detailsLoading = false;
        }
        await _logStreamer.UpdateAsync(_selectedStep);
        if (!IsCurrentLoad(runId, generation)) return;
        _live = new PipelineRunLiveConnection(HubFactory, Logger, runId,
            () => LiveChildRunIds(_childRunCache), ReloadRunAsync, RefreshQueueAsync, InvokeAsync,
            () => AllStepsAcrossChildren().Any(step => step.Status == TaskExecutionStatus.Assigned),
            () => RunInProgress, () => _ = InvokeAsync(StateHasChanged));
        await _live.StartAsync();
    }

    private bool IsCurrentLoad(int runId, int generation) =>
        _loadSession.IsCurrent(generation) && RunId == runId;

    private void ReassertBreadcrumb()
    {
        if (_run is null) return;
        var runItem = new BreadcrumbItem($"#{_run.Id} {L["PipelineRun"]}");
        if (_run.ProjectId is { } projectId)
        {
            Breadcrumb.Set(
                new BreadcrumbItem(L["Projects"], "/projects"),
                new BreadcrumbItem(_run.ProjectName ?? $"{L["Project"]} #{projectId}", $"/projects/{projectId}/overview"),
                new BreadcrumbItem(L["Pipelines"], $"/projects/{projectId}/pipelines"),
                new BreadcrumbItem(_run.PipelineName, PipelineHref),
                runItem);
            return;
        }

        Breadcrumb.Set(
            new BreadcrumbItem(L["Pipelines"], "/pipelines"),
            new BreadcrumbItem(_run.PipelineName, PipelineHref),
            runItem);
    }

    private async Task<bool> ResetRunPageAsync(int runId, int generation)
    {
        if (_live is not null)
        {
            await _live.DisposeAsync();
            _live = null;
            if (!IsCurrentLoad(runId, generation)) return false;
        }
        if (_logStreamer is not null)
        {
            await _logStreamer.DisposeAsync();
            _logStreamer = null;
            if (!IsCurrentLoad(runId, generation)) return false;
        }
        ResetRunState();
        return true;
    }

    private async Task<PipelineRunDto?> LoadRootRunAsync(int runId, bool refresh, CancellationToken ct)
    {
        try
        {
            return !refresh && _pageCache.TryGetRun(runId, out var cached)
                ? cached
                : await Api.Pipelines.GetPipelineRunAsync(runId, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private void PrepareRootRun()
    {
        _run = NormalizeRunCompletion(_run!);
        _pageCache.StoreRun(_run);
        BuildStages();
        ProjectNav.Set(_run.ProjectId);
        ReassertBreadcrumb();
    }

    private async Task<bool> EnrichRunAsync(int runId, int generation, bool approvalLoaded, CancellationToken ct)
    {
        var rootRun = _run!;
        var repositoryTask = PipelineRunRepositoryResolver.ResolveAsync(Api, rootRun, ct);
        var releasesTask = Api.Projects.GetReleasesByRunAsync(runId, ct);
        var aiResultsTask = PipelineRunAiResults.LoadAsync(Api, runId, ct);
        var failedLogsTask = LoadFailedStepLogsAsync();
        // R-43: the same summary the pipeline page shows, for its average duration of successes.
        var summaryTask = PipelineRunsSummary.LoadAsync(Api, rootRun.PipelineId);
        // Approval requires only the run ID and status, so it can load independently of child-run
        // metrics.
        var approvalTask = approvalLoaded ? Task.CompletedTask : _approval!.LoadAsync(rootRun, ct);
        var (children, mergedRun) = await PipelineRunChildAggregator.LoadAsync(rootRun, Api, _pageCache, ct);
        if (!IsCurrentLoad(runId, generation)) return false;
        _childRunCache.Clear();
        foreach (var child in children) _childRunCache[child.Key] = child.Value;
        _run = mergedRun;
        // The children decide which tabs the run has (coverage, tests, lint, quality): the tabs can
        // show now, while the gate, the baselines and the trends are still on their way.
        _childrenLoaded = true;
        await InvokeAsync(StateHasChanged);
        await Task.WhenAll(
            _baselines.LoadAsync(_childRunCache.Values.Prepend(_run)),
            LoadGateAsync(ct),
            _schemaState.LoadAsync(Api, runId, ct),
            _metrics.LoadAsync(Api, runId, _run),
            failedLogsTask,
            approvalTask);
        if (!IsCurrentLoad(runId, generation)) return false;
        _repoId = await repositoryTask;
        _releases = await LoadReleasesAsync(releasesTask).ConfigureAwait(false);
        _aiResults = await aiResultsTask;
        _pipelineRunsSummary = await summaryTask;
        if (!IsCurrentLoad(runId, generation)) return false;
        return await AutoSelectStepAsync(runId, generation).ConfigureAwait(false);
    }

    private static async Task<List<ReleaseDto>> LoadReleasesAsync(Task<List<ReleaseDto>> releasesTask)
    {
        try { return await releasesTask; }
        catch (HttpRequestException) { return []; }
    }

    private async Task<bool> AutoSelectStepAsync(int runId, int generation)
    {
        var requested = RequestedStep();
        _appliedStepId = StepId;
        if (requested is not null) _userPinnedStep = true;
        var autoSelect = requested
            ?? _stages.SelectMany(s => s.Steps)
            .FirstOrDefault(s => s.Status == TaskExecutionStatus.Running && s.TriggeredRunId is null)
            ?? _stages.SelectMany(s => s.Steps)
                .LastOrDefault(s => s.TriggeredRunId is null
                                    && s.Status is TaskExecutionStatus.Failed or TaskExecutionStatus.Success);
        if (autoSelect is null) return true;
        _selectedStep = autoSelect;
        await _logStreamer!.UpdateAsync(_selectedStep);
        if (!IsCurrentLoad(runId, generation)) return false;
        if (!autoSelect.TaskId.HasValue) return true;
        await LoadTaskLogSnapshotAsync(autoSelect.TaskId.Value);
        return IsCurrentLoad(runId, generation);
    }

    // The ?step= step, in this run or a child it triggered; a trigger step has no log of its own.
    private PipelineStepRunDto? RequestedStep() => StepId is { } wanted
        ? AllStepsAcrossChildren().FirstOrDefault(step => step.Id == wanted && step.TriggeredRunId is null)
        : null;

    private void ResetRunState()
    {
        _run = null;
        _detailsLoading = false;
        _childrenLoaded = false;
        _metrics = new PipelineRunMetrics();
        _gateState.Clear();
        _schemaState.Clear();
        _releases = [];
        _aiResults = [];
        _repoId = null;
        _stages = [];
        _selectedTab = 0;
        _expandedMatrixGroups.Clear();
        _childRunCache.Clear();
        _logWindow.Clear();
        _completeLogLoading = false;
        _selectedStep = null;
        _userPinnedStep = false;
        _logVarRegex = null;
        _logSearch = string.Empty;
        _showCommand = false;
        _showResults = true;
        _peekName = null;
        _yamlWrap = false;
        _logsLoading = false;
        LogFollow.Follow();
        _rerunning = false;
        _retrying = false;
        _cancelling = false;
        _approval?.Reset();
    }

    // S-DES-11: regex over the run's known variable names (built by PipelineRunLogView) so they can be
    // accented inside log lines; consumed by the markup via PipelineRunLogView.HighlightLogVariables.
    private Regex? _logVarRegex;

    /// <summary>A run is genuinely "in progress" only when it has no end timestamp AND a non-terminal
    /// status - so an orphaned run (terminal status, missing CompletedAt) is not treated as live.</summary>
    private bool RunInProgress => _run is not null && _run.CompletedAt is null && !IsTerminal(_run.Status);

    /// <summary>A run is an orchestration when at least one step is a <c>type: trigger</c> child launcher
    /// (<see cref="PipelineStepRunDto.TriggeredRunId"/>). Its Journaux tab drops the 22rem split for a
    /// full-width run tree (the mockup layout): trigger nodes have no logs of their own, so the tree - not
    /// a log pane - is the primary content.</summary>
    private bool IsOrchestration => _run?.Steps.Any(s => s.TriggeredRunId is not null) == true;

    // Orchestration: the triggered child runs, preloaded at load time (keyed by run id). Used to (a) bubble
    // the children's coverage/lint/test/complexity data up onto this run's tabs+tiles, and (b) let the run
    // timeline expand instantly and reveal a selected child step without a per-click fetch.
    private readonly Dictionary<int, PipelineRunDto> _childRunCache = [];
    private IReadOnlyDictionary<int, PipelineRunDto> ChildRuns => _childRunCache;

    /// <summary>Fetch the triggered child runs and merge their result data onto <c>_run</c> so an
    /// orchestration run surfaces the same Coverage/Lint/Code Quality/Test Results tabs and overview tiles
    /// as the child pipelines that actually produced them. Delegated to <see cref="PipelineRunChildAggregator"/>.</summary>
    private async Task LoadChildRunsAndAggregateAsync(CancellationToken ct = default)
    {
        _childRunCache.Clear();
        if (_run is null || !IsOrchestration) return;

        var (children, merged) = await PipelineRunChildAggregator.LoadAsync(_run, Api, _pageCache, ct);
        foreach (var kv in children) _childRunCache[kv.Key] = kv.Value;
        _run = merged;
    }

    private void BuildStages()
    {
        if (_run is null) return;
        _logVarRegex = PipelineRunLogView.BuildLogVarRegex(_run);
        _stages = RunStageBuilder.Build(_run);
    }

    internal async Task ReloadRunAsync()
    {
        _run = await Api.Pipelines.GetPipelineRunAsync(RunId);
        if (_run is not null)
        {
            _run = NormalizeRunCompletion(_run);
            BuildStages();
            try { _releases = await Api.Projects.GetReleasesByRunAsync(RunId); }
            catch (HttpRequestException) { /* keep prior releases on a transient failure */ }
            await LoadChildRunsAndAggregateAsync(_loadSession.Token);
            await _baselines.LoadAsync(_childRunCache.Values.Prepend(_run));
            await LoadGateAsync(_loadSession.Token);
            await _schemaState.LoadAsync(Api, RunId, _loadSession.Token);
            await _approval!.LoadAsync(_run, _loadSession.Token);
            // A child run may have just appeared (its trigger step fired) - join its group so its own
            // step events keep this view live from here on.
            if (_live is not null) await _live.JoinGroupsAsync();
            // A manual selection is sticky across live reloads. Refresh the selected DTO by its persisted
            // step id, but never replace it with a different running parent/child step. If the selected
            // step disappeared (for example after a run retry rebuilt its steps), release the pin and
            // resume automatic execution following.
            var pinnedStep = _userPinnedStep && _selectedStep is not null
                ? AllStepsAcrossChildren().FirstOrDefault(step => step.Id == _selectedStep.Id)
                : null;
            if (pinnedStep is not null)
            {
                _selectedStep = pinnedStep;
            }
            else
            {
                _userPinnedStep = false;
                // Follow execution task-to-task, including into a triggered child run: prefer a running
                // step that actually has logs over the logless trigger step.
                var runningStep = AllStepsAcrossChildren().FirstOrDefault(step =>
                                      step.Status == TaskExecutionStatus.Running && step.TaskId is not null)
                                  ?? _stages.SelectMany(stage => stage.Steps).FirstOrDefault(step =>
                                      step.Status == TaskExecutionStatus.Running);
                if (runningStep is not null)
                {
                    _selectedStep = runningStep;
                    if (_logStreamer is not null) await _logStreamer.UpdateAsync(_selectedStep);
                    // First time we follow this task, seed its history (the log hub streams only NEW lines
                    // from the join point, it does not replay).
                    if (runningStep.TaskId is { } tid && !_logWindow.HasSnapshot(tid))
                    {
                        await LoadTaskLogSnapshotAsync(tid);
                    }
                }
            }
            await LoadFailedStepLogsAsync();
        }
        else
        {
        }
        if (_logStreamer is not null) await _logStreamer.UpdateAsync(_selectedStep);
        StateHasChanged();
    }

    internal async Task RefreshQueueAsync()
    {
        if (_run is null) return;
        _run = await PipelineRunQueueRefresh.RefreshAsync(
            Api, _run, _childRunCache, _loadSession.Token);
        BuildStages();
        if (_selectedStep is not null)
            _selectedStep = AllStepsAcrossChildren().FirstOrDefault(step => step.Id == _selectedStep.Id)
                ?? _selectedStep;
        StateHasChanged();
    }

    private IEnumerable<PipelineStepRunDto> AllStepsAcrossChildren() =>
        PipelineRunPresentation.AllSteps(_stages, _childRunCache);

    internal static IReadOnlyCollection<int> LiveChildRunIds(IReadOnlyDictionary<int, PipelineRunDto> children) =>
        PipelineRunPresentation.LiveChildRunIds(children);

    public async ValueTask DisposeAsync()
    {
        _loadSession.Dispose();

        if (_live is not null)
            await _live.DisposeAsync();

        if (_logStreamer is not null)
            await _logStreamer.DisposeAsync();

        _logFollow?.Dispose();
    }

    private void ToggleMatrixGroup(string key)
    {
        if (!_expandedMatrixGroups.Remove(key))
            _expandedMatrixGroups.Add(key);
    }

    private string? GitBranch => PipelineRunPresentation.GitBranch(_run);

    /// <summary>Application version this run built (APP_VERSION), shown under the Release tile.</summary>
    private string? AppVersion => _run is null ? null : BuiltAppVersion(_run);

    // Recette R-16: a notice (PipelineRunNotices) is information, not a warning of the run: it is neither
    // blocking, nor orange, nor counted in "Avertissements (n)".
    // A warning is either a sentence written before problems had codes or a stored problem
    // (StoredProblem): it is classified on its English sentence and shown in the user's language.
    private IReadOnlyList<string> BlockingWarnings =>
        _run is null ? [] : _run.Warnings.Where(w => PipelineRunNotices.CodeOf(w) is null && IsBlockingWarning(StoredProblem.English(w))).ToList();

    private IReadOnlyList<string> InfoWarnings =>
        _run is null ? [] : _run.Warnings.Where(w => PipelineRunNotices.CodeOf(w) is null && !IsBlockingWarning(StoredProblem.English(w))).ToList();

    private IReadOnlyList<BlockingWarningView> BlockingWarningViews =>
        _run is null ? [] : BlockingWarnings.Select(w => ToBlockingView(StoredProblem.English(w)) with { Message = SayWarning(w) }).ToList();

    /// <summary>A warning of the run in the user's language.</summary>
    private string SayWarning(string warning) => LaunchProblemText.DescribeStored(warning, L);

    /// <summary>The codes of the run's notices, each once.</summary>
    private IReadOnlyList<string> RunNoticeCodes =>
        _run is null ? [] : [.. _run.Warnings.Select(PipelineRunNotices.CodeOf).OfType<string>().Distinct(StringComparer.Ordinal)];

    private string RunNoticeText(string code) => code switch
    {
        PipelineRunNotices.BaselineAbsent => L["RunNoticeBaselineAbsent"].Value,
        PipelineRunNotices.ResumeWindowExpired => L["RunNoticeResumeWindowExpired"].Value,
        _ => string.Format(L["RunNoticeUnknown"].Value, code)
    };

    private string? RunNoticeHelp(string code) => code switch
    {
        PipelineRunNotices.BaselineAbsent => L["RunNoticeBaselineAbsentHelp"].Value,
        _ => null
    };

    /// <summary>Why nothing is advancing, this run's own wait and its loaded children's. Only while the
    /// run is live: a finished run has nothing left to wait for.</summary>
    private IReadOnlyList<PipelineRunWaitingSummary.WaitView> WaitViews =>
        RunInProgress ? PipelineRunWaitingSummary.Build(_run, _childRunCache) : [];

    /// <summary>The candidate's sealed verdict, from this run or from the child that sealed it.</summary>
    private PipelineRunAssuranceVerdict.Verdict? AssuranceVerdict =>
        PipelineRunAssuranceVerdict.Read(_run, _childRunCache);

    private bool HasFailedSteps => _run?.Steps.Any(s => s.Status == TaskExecutionStatus.Failed) == true;

    private bool CanRerun => PipelineRunRerunCoordinator.CanRerun(_run);

    private PipelineRunCommandCoordinator Commands =>
        _commandCoordinator ??= new(Api, Dialog, Toast, L, Js, Veil);

    private async Task OnRerunClick(string? mode)
    {
        if (_run is null || _rerunning) return;

        _rerunning = true;
        try
        {
            _rerunCoordinator ??= new PipelineRunRerunCoordinator(Api, Nav, Toast, Dialog, L);
            await _rerunCoordinator.RerunAsync(_run, mode);
        }
        finally
        {
            _rerunning = false;
        }
    }

    private bool HasLintTab => _run?.LintSummary is not null || LintSteps.Count > 0;

    private bool HasYamlTab => !string.IsNullOrEmpty(_run?.YamlSnapshot);

    private bool ShowGateTab => !RunInProgress && _gateState.HasResult;

    private List<string> TabSlugs() => PipelineRunPresentation.TabSlugs(_run, ShowGateTab, HasYamlTab, HasLintTab, _schemaState.HasGate);

    /// <summary>The tabs wait for the child runs (they decide which tabs exist), and for the tab a
    /// shared <c>?tab=</c> link names, or they would open on the overview and stay there.</summary>
    private bool TabsReady => _childrenLoaded
        && (!_detailsLoading || string.IsNullOrEmpty(Tab) || TabSlugs().Contains(Tab, StringComparer.OrdinalIgnoreCase));

    // Overview cross-link tiles that jump into a tab. They navigate (push history) so the URL stays the
    // source of truth for UrlSyncedTabs; the guard against the current tab avoids duplicate entries.
    private void NavigateToTab(string slug)
    {
        var idx = TabSlugs().IndexOf(slug);
        if (idx < 0 || idx == _selectedTab) return;
        Nav.NavigateTo(Nav.GetUriWithQueryParameter("tab", idx == 0 ? null : slug));
    }

    /// <summary>The address that opens the tab <paramref name="slug"/> of this run, for the overview tiles
    /// that are links (<c>OmniCard.Href</c>); null when the run has no such tab, so the tile stays plain.</summary>
    private string? TabHref(string slug)
    {
        var idx = TabSlugs().IndexOf(slug);
        return idx < 0 ? null : Nav.GetUriWithQueryParameter("tab", idx == 0 ? null : slug);
    }

    private Task LoadGateAsync(CancellationToken ct)
    {
        if (_run is null || RunInProgress)
        {
            _gateState.Clear();
            return Task.CompletedTask;
        }

        return _gateState.LoadAsync(Api, _run, _childRunCache.Values, _pageCache, ct);
    }

    private IReadOnlyList<PipelineStepRunDto> LintSteps =>
        PipelineRunPresentation.LintSteps(_run, _childRunCache);

    /// <summary>The logs of the coverage or lint step, asked for by its result tab.</summary>
    private async Task LoadResultStepLogsAsync(int taskId)
    {
        try { _logWindow.ApplySnapshot(taskId, await Api.Monitoring.GetTaskLogsAsync(taskId) ?? [], requested: null); }
        catch (HttpRequestException) { _stepLogsCache[taskId] = []; }
        StateHasChanged();
    }

    private async Task RetryFailedAsync()
    {
        if (_run is null || _retrying) return;
        _retrying = true;
        try
        {
            if (await Commands.RetryFailedAsync(_run))
                await ReloadRunAsync();
        }
        finally
        {
            _retrying = false;
        }
    }

    private bool _cancelling;

    // Cancel an in-progress run (kills its running steps). Confirm first, then reload so the UI reflects
    // the Cancelled state. A run that finished server-side in the meantime comes back NotFound - reload
    // anyway so the view catches up to the terminal state.
    private async Task CancelRunAsync()
    {
        if (_run is null || _cancelling || !RunInProgress) return;
        if (!await Commands.ConfirmCancellationAsync()) return;
        _cancelling = true;
        try
        {
            await Commands.CancelAsync(_run);
            await ReloadRunAsync();
        }
        finally
        {
            _cancelling = false;
        }
    }

    // --- Log scrolling / auto-follow (S: live test runs emit thousands of lines) ---
    private ElementReference _logsTerminal;
    private PipelineRunLogFollow LogFollow => _logFollow ??= new PipelineRunLogFollow(Js, () => InvokeAsync(StateHasChanged));
    private PipelineRunLogFollow? _logFollow;

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (_selectedStep?.TaskId is not { } taskId) return Task.CompletedTask;
        return LogFollow.AfterRenderAsync(_logsTerminal, _stepLogsCache.GetValueOrDefault(taskId)?.Count ?? 0);
    }

    private Task ScrollLogsToTopAsync() => LogFollow.ToTopAsync(_logsTerminal);
    private Task ScrollLogsToBottomAsync() => LogFollow.ToBottomAsync(_logsTerminal);
    private Task ToggleFollowLogs() => LogFollow.ToggleAsync(_logsTerminal);

    private Task ShowVariables() =>
        _run is null || _run.ResolvedVariables.Count == 0
            ? Task.CompletedTask
            : Commands.ShowVariablesAsync(_run);

    // R-37: parsed from the run's definition snapshot, once per snapshot (the page re-renders on every live update).
    private string? _usedResourcesSnapshot;
    private PipelineRunResources.Used _usedResources = PipelineRunResources.Used.None;

    private PipelineRunResources.Used UsedResources
    {
        get
        {
            if (!ReferenceEquals(_usedResourcesSnapshot, _run?.YamlSnapshot))
            {
                _usedResourcesSnapshot = _run?.YamlSnapshot;
                _usedResources = PipelineRunResources.Of(_usedResourcesSnapshot, _run?.ProjectName, _run?.ProjectId);
            }
            return _usedResources;
        }
    }

    private Task ShowParameters() =>
        _run is null || _run.Parameters.Count == 0
            ? Task.CompletedTask
            : Commands.ShowParametersAsync(_run);

    private string GetNextConnectorStatus(int stageIndex) =>
        PipelineRunPresentation.NextConnectorStatus(_stages, stageIndex);

    // --- Step log viewer (split view) ---
    private PipelineStepRunDto? _selectedStep;
    private bool _userPinnedStep;
    internal PipelineStepRunDto? SelectedStep => _selectedStep;
    internal bool IsStepSelectionPinned => _userPinnedStep;
    private string _logSearch = string.Empty;

    // The executed command can echo secrets/long argv, so it is masked by default and revealed on
    // demand; the output (results) is shown by default but can be collapsed to focus on the command.
    private bool _showCommand;
    private bool _showResults = true;

    // S-UX-17: copy the selected step's full log to the clipboard.
    private Task CopyLogsAsync() =>
        _selectedStep?.TaskId is { } taskId
        && _stepLogsCache.GetValueOrDefault(taskId) is { Count: > 0 } logs
            ? Commands.CopyLogsAsync(logs)
            : Task.CompletedTask;

    private string FailedRunReason => string.Join(", ", FailedSteps.Select(step =>
        step.ExitCode is { } code ? string.Format(L["FailedStepWithExit"], step.StepName, code) : step.StepName));
    private IReadOnlyList<PipelineStepRunDto> FailedSteps =>
        // R-31: a step that timed out is a failure too; it used to be missing here, so a run that failed on a
        // timeout listed no failed step and said nothing.
        _run?.Steps.Where(step => step.Status is TaskExecutionStatus.Failed or TaskExecutionStatus.Timeout && !step.IsSystem).ToList() ?? [];
    private Task LoadFailedStepLogsAsync() => PipelineRunLogView.LoadFailedStepLogsAsync(FailedSteps, Api, _logWindow);
    private Task DownloadArtifactAsync(PipelineArtifactDto artifact) => Commands.DownloadArtifactAsync(artifact);
    private Task OpenAiResultAsync(AiRunResultDto result) => PipelineRunAiResults.OpenAsync(Dialog, L, result);
    private Task CopyYamlAsync() => string.IsNullOrEmpty(_run?.YamlSnapshot) ? Task.CompletedTask : Commands.CopyYamlAsync(_run.YamlSnapshot);
    private string? _peekName;
    private bool _yamlWrap;
    private void ClosePeek() => _peekName = null;
    // Live lines are bounded per task; complete logs are read on demand.
    private readonly PipelineRunLogWindow _logWindow = new();
    private Dictionary<int, List<TaskLogDto>> _stepLogsCache => _logWindow.Lines;
    internal PipelineRunLogWindow LogWindow => _logWindow;
    private bool _completeLogLoading;
    private bool _logsLoading;
    internal async Task SelectStep(PipelineStepRunDto step)
    {
        _selectedStep = step;
        _userPinnedStep = true;
        LogFollow.Rearm();
        NavigateToTab("logs");
        if (step.TaskId is null)
        {
            if (_logStreamer is not null) await _logStreamer.UpdateAsync(_selectedStep);
            return;
        }
        var taskId = step.TaskId.Value;
        if (step.Status == TaskExecutionStatus.Running) _logWindow.Forget(taskId);
        if (_logStreamer is not null) await _logStreamer.UpdateAsync(_selectedStep);
        // Streamed lines may omit earlier history, so the complete log must still be read.
        if (_logWindow.HasSnapshot(taskId)) return;
        _logsLoading = true;
        StateHasChanged();
        await LoadTaskLogSnapshotAsync(taskId);
        _logsLoading = false;
    }
    private async Task LoadTaskLogSnapshotAsync(int taskId)
    {
        List<TaskLogDto> snapshot;
        try { snapshot = await Api.Monitoring.GetTaskLogTailAsync(taskId, PipelineRunLogWindow.LiveWindow); }
        catch (HttpRequestException) { snapshot = []; }
        // Lines streamed while the snapshot was read are kept (merged by id).
        _logWindow.ApplySnapshot(taskId, snapshot, PipelineRunLogWindow.LiveWindow);
    }

    /// <summary>"Journal complet" reads the selected step's complete log, page by page.</summary>
    internal async Task ShowCompleteLogAsync()
    {
        if (_selectedStep?.TaskId is not { } taskId || _completeLogLoading) return;
        _completeLogLoading = true;
        try
        {
            await _logWindow.LoadCompleteAsync(taskId, page =>
                Api.Monitoring.GetTaskLogPageAsync(taskId, page, PipelineRunLogWindow.PageSize, _loadSession.Token));
        }
        catch (HttpRequestException) { Toast.Error("Logs", "CompleteLogLoadFailed"); }
        catch (OperationCanceledException) when (_loadSession.Token.IsCancellationRequested) { }
        finally { _completeLogLoading = false; }
    }
}
