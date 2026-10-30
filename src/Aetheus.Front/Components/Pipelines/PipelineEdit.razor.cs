// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineEdit : IAsyncDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private PipelineRunGate RunGate { get; set; } = default!;
    [Inject] private PipelineRunDialogCoordinator RunDialogs { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;
    [Inject] private PageStateCache PageState { get; set; } = default!;
    [Inject] private PipelineImportState ImportState { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private ILogger<PipelineEdit> Logger { get; set; } = default!;

    [Parameter] public int? Id { get; set; }

    [SupplyParameterFromQuery] public int? ProjectId { get; set; }
    [SupplyParameterFromQuery] public int? ServerId { get; set; }
    [SupplyParameterFromQuery(Name = LocalReturnUrl.Parameter)] public string? ReturnUrl { get; set; }
    [SupplyParameterFromQuery] public int? EnvironmentId { get; set; }
    [SupplyParameterFromQuery] public int? ProjectServerId { get; set; }
    [SupplyParameterFromQuery(Name = "templateId")] public int? TemplateId { get; set; }

    private PipelineDto? _pipeline;
    private PipelineDefinitionLiveSync? _definitionSync;
    private bool _changedElsewhere;
    private UpdatePipelineRequest? _lastSubmitted;
    // The repository file the review dialog showed when the save replaces lines Aetheus had not seen.
    private string? _confirmedRepositoryYamlSha256;
    private PipelineSourceDto? _source;
    private List<string> _sourceBranches = [];
    private PipelineModel _model = new();
    private string? _savedSourceBranch;
    private List<PipelineRunDto> _runs = [];
    private Aetheus.Front.Components.Shared.AetheusDataGrid<PipelineRunDto>? _runsGrid;
    private int _runsTotalCount;
    private int _runsPage = 1;
    private int _runsPageSize = PipelineRunsSummary.SummaryPageSize;

    /// <summary>Runs tab tiles, from the first block of runs: the grid scrolls virtually.</summary>
    private PipelineRunsSummary RunsSummary => PipelineRunsSummary.Create(_firstRunsPage);

    private List<PipelineRunDto> _firstRunsPage = [];

    /// <summary>Sort and column filters currently applied to the runs grid. Held across reloads so a
    /// refresh, a live update or a launch re-fetches the page the reader is actually looking at.</summary>
    private PipelineRunPaginationRequest? _runsQuery;

    private PipelineRunsLiveConnection? _liveRuns;
    private List<ProjectDto> _projects = [];
    private bool _isNew => Id is null or 0;
    private bool _saving;
    private bool _loading;
    private bool _notFound;
    private bool _forbidden;
    private bool _deleting;
    private bool _validating;
    private bool? _yamlValid;
    private int _selectedTab;
    private int? _previousId = int.MinValue;
    private int _loadGeneration;
    private MonacoEditor? _monacoEditor;
    private bool _suggestionsNeeded;
    private bool _showDiff;
    // The pipeline whose read-only definition is peeked beside the editor (Ctrl+Click on a `pipeline:` ref).
    private string? _peekName;
    private string _savedYaml = string.Empty;
    private List<PipelineTemplateSummaryDto> _templates = [];
    private string? _baseTemplateYaml;
    private int? _selectedTemplateId;
    private readonly PipelineTemplateParameterState _templateParameters = new();
    private List<string> _serverNames = [];
    private List<string> _libraryNames = [];
    private List<string> _vaultNames = [];
    private bool _canTemplateWrite;
    private PipelineFleetItemDto? _fleetItem;

    private string BackHref => PipelineRunPresentation.EditBackHref(ProjectId, ServerId);

    private bool HasTemplateReference => PipelineTemplateReferenceHelper.Parse(_model.YamlDefinition) is not null;
    private bool HasLegacyTemplateReference =>
        PipelineTemplateReferenceHelper.Parse(_model.YamlDefinition) is { Version: null };
    private PipelineTemplateEditorCoordinator TemplateEditor => new(Api);
    private PipelineYamlEditorCoordinator YamlEditor => new(Api, L, Logger);

    protected override void OnInitialized()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshTemplatePermission();
    }

    private async Task LoadBaseTemplateYamlAsync()
        => _baseTemplateYaml = await TemplateEditor.LoadBaseYamlAsync(_model.YamlDefinition, _templates);

    private async Task PinLegacyTemplateAsync()
    {
        var pinnedYaml = TemplateEditor.Pin(_model.YamlDefinition, _templates);
        if (pinnedYaml is null) return;
        _model.YamlDefinition = pinnedYaml;
        await LoadBaseTemplateYamlAsync();
        _suggestionsNeeded = true;
    }

    private Task<List<PipelineTemplateSummaryDto>> LoadTemplatesSafeAsync() =>
        TemplateEditor.LoadTemplatesAsync();

    private void OnPermissionsChanged()
    {
        RefreshTemplatePermission();
        _ = InvokeAsync(StateHasChanged);
    }

    private void RefreshTemplatePermission() =>
        _canTemplateWrite = Permissions.CanWrite(ResourceType.PipelineTemplate);

    private async Task OnTemplateSelected(object? value)
    {
        if (value is not int templateId) return;
        var selection = await TemplateEditor.SelectAsync(templateId, _model.Name);
        if (selection is null)
        {
            _selectedTemplateId = null;
            _templateParameters.Clear();
            return;
        }
        _model.YamlDefinition = _templateParameters.Select(selection);
        _baseTemplateYaml = selection.BaseYaml;
        _suggestionsNeeded = true;
        StateHasChanged();
    }

    private void OnTemplateParameterChanged(string name, string? value)
    {
        _model.YamlDefinition = _templateParameters.Update(name, value);
        _suggestionsNeeded = true;
    }

    private string TemplateParameterValue(string name) => _templateParameters.GetValue(name);

    private void OnExtractTemplate() => Nav.NavigateTo($"/pipelines/{Id}/template/extract");
    private void OnPromoteTemplate() => Nav.NavigateTo($"/pipelines/{Id}/template/promote");
    private void OnUpdateTemplate()
    {
        if (Id is not null && _fleetItem?.LatestVersion is { } version)
            Nav.NavigateTo($"/pipelines/{Id}/template/update/{version}");
    }

    private async Task LoadFleetItemAsync()
    {
        if (Id is null || _pipeline is null)
        {
            _fleetItem = null;
            return;
        }
        _fleetItem = await TemplateEditor.FindFleetItemAsync(Id.Value);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (Id == _previousId)
        {
            // Same pipeline, only a query/tab/back navigation: BreadcrumbService clears its items on
            // every LocationChanged, so re-assert them (and keep the project nav pinned) without
            // reloading. Setting the crumb only on Id-change stranded an empty breadcrumb after a
            // same-component navigation (browser back to this page, tab switch, ...).
            ReassertNavContext();
            return;
        }
        _previousId = Id;
        var generation = Interlocked.Increment(ref _loadGeneration);
        await ResetEditorStateAsync();
        if (_isNew)
            await LoadNewPipelineAsync(generation);
        else
            await LoadExistingPipelineAsync(generation);
        if (generation == _loadGeneration) ReassertNavContext();
    }

    /// <summary>R-34: the page of a pipeline that no longer exists, a state of its own and not a failure.</summary>
    /// <summary>With <paramref name="forbidden"/>, the pipeline exists but the API refuses it to the caller: access denied.</summary>
    private void ShowNotFound(bool forbidden = false)
    {
        _pipeline = null;
        _notFound = !forbidden;
        _forbidden = forbidden;
        _loading = false;
    }

    private async Task StopLiveUpdatesAsync()
    {
        if (_liveRuns is not null) await _liveRuns.DisposeAsync();
        if (_definitionSync is not null) await _definitionSync.DisposeAsync();
        _liveRuns = null;
        _definitionSync = null;
    }

    private async Task ResetEditorStateAsync()
    {
        await StopLiveUpdatesAsync();
        _deleting = false;
        _changedElsewhere = false;
        _notFound = false;
        _forbidden = false;
        _pipeline = null;
        _source = null;
        _sourceBranches = [];
        _model = new PipelineModel();
        _runs = [];
        _runsTotalCount = 0;
        _runsPage = 1;
        _templates = [];
        _baseTemplateYaml = null;
        _selectedTemplateId = null;
        _templateParameters.Clear();
        _serverNames = [];
        _libraryNames = [];
        _vaultNames = [];
        _fleetItem = null;
        _peekName = null;
        _savedYaml = string.Empty;
        _yamlValid = null;
        _showDiff = false;
    }

    private async Task LoadExistingPipelineAsync(int generation)
    {
        // A return shows at once what the page last showed, then refreshes behind it; the page-wide
        // loader is for a first visit only.
        var address = $"pipelines/{Id}";
        var ticket = PageState.Begin();
        var restored = PageState.TryRestore<PipelinePageState>(address, out var state);
        if (restored) ApplyPageState(state);
        _loading = !restored;
        var lookupTask = Api.Pipelines.LookUpPipelineAsync(Id!.Value);
        var runsTask = Api.Pipelines.GetPipelineRunsPagedAsync(Id.Value, 1, _runsPageSize);
        var projectsTask = Api.Projects.GetAllProjectsAsync();
        var sourceTask = Api.Pipelines.GetPipelineSourceAsync(Id.Value);
        var templatesTask = LoadTemplatesSafeAsync();
        var siblings = Task.WhenAll(runsTask, projectsTask, sourceTask, templatesTask);
        var (found, forbidden) = await lookupTask;
        if (found is null)
        {
            // R-34: a pipeline that no longer exists (deleted, or a wrong address) is a page of its own,
            // not a failure. The requests started beside it answer 404 for it as well: they are
            // awaited only so that nothing is left unobserved, their outcome is of no use. A refusal
            // (another organization's pipeline, which the route check cannot tell from the caller's grants
            // alone) ends the same way, as the access-denied state and never as an exception.
            await siblings.ContinueWith(static _ => { }, TaskScheduler.Default);
            if (generation != _loadGeneration) return;
            PageState.Forget<PipelinePageState>(address);
            ShowNotFound(forbidden);
            return;
        }
        await siblings;
        if (generation != _loadGeneration) return;
        // What the reader typed or filtered while the refresh ran stays theirs.
        var editorUntouched = !restored || string.Equals(_model.YamlDefinition, _savedYaml, StringComparison.Ordinal);
        _pipeline = found;
        if (!restored || _runsQuery is null) ApplyRunsPage(await runsTask, 1, _runsPageSize);
        _projects = await projectsTask;
        _templates = await templatesTask;
        _source = await sourceTask;
        if (_source is { RepositoryId: > 0 })
        {
            var branches = await Api.Git.GetGitBranchesAsync(_source.RepositoryId);
            if (generation != _loadGeneration) return;
            _sourceBranches = branches.Select(branch => branch.Name).ToList();
        }
        if (_pipeline is not null)
        {
            if (editorUntouched) ApplyPipelineModel(_pipeline);
            await LoadBaseTemplateYamlAsync();
            await LoadFleetItemAsync();
            if (generation != _loadGeneration) return;
            PageState.Remember(ticket, address, new PipelinePageState(_pipeline, _firstRunsPage, _runsTotalCount, _projects,
                _templates, _source, _sourceBranches, _baseTemplateYaml, _fleetItem));
        }
        else
        {
            PageState.Forget<PipelinePageState>(address);
        }
        _loading = false;
        _suggestionsNeeded = true;
        await EnsureHubAsync();
        await _liveRuns!.SyncGroupsAsync();
    }

    private void ApplyPageState(PipelinePageState state)
    {
        _pipeline = state.Pipeline;
        ApplyRunsPage(new PaginatedResult<PipelineRunDto> { Items = state.FirstRuns, TotalCount = state.RunsTotalCount },
            1, _runsPageSize);
        _projects = state.Projects;
        _templates = state.Templates;
        _source = state.Source;
        _sourceBranches = state.SourceBranches;
        ApplyPipelineModel(state.Pipeline);
        _baseTemplateYaml = state.BaseTemplateYaml;
        _fleetItem = state.FleetItem;
    }

    private void ApplyPipelineModel(PipelineDto pipeline)
    {
        _model = new PipelineModel
        {
            Name = pipeline.Name,
            Description = pipeline.Description,
            YamlDefinition = pipeline.YamlDefinition,
            ProjectId = pipeline.ProjectId,
            SourceBranch = pipeline.SourceBranch ?? _source?.Branch
                ?? _projects.FirstOrDefault(project => project.Id == pipeline.ProjectId)?.DefaultBranch,
            EnvironmentId = pipeline.EnvironmentId,
            ProjectServerId = pipeline.ProjectServerId,
            AiRunnerProfileId = pipeline.AiRunnerProfileId
        };
        _savedYaml = pipeline.YamlDefinition;
        _savedSourceBranch = _model.SourceBranch;
    }

    private async Task LoadNewPipelineAsync(int generation)
    {
        var projectsTask = Api.Projects.GetAllProjectsAsync();
        var templatesTask = Api.PipelineTemplates.GetPipelineTemplatesAsync();
        await Task.WhenAll(projectsTask, templatesTask);
        if (generation != _loadGeneration) return;
        _projects = await projectsTask;
        _templates = await templatesTask;
        SelectInitialOwner();
        _model.YamlDefinition = """
                name: my-pipeline
                trigger: manual
                stages:
                  - name: Validate
                    jobs:
                      - name: validate
                        agent: default
                        steps:
                          - name: Check repository
                            shell: git status --short
            """;
        _suggestionsNeeded = true;
        if (ImportState.Consume() is { } import)
        {
            _model.YamlDefinition = import.Yaml;
            if (!string.IsNullOrWhiteSpace(import.Name)) _model.Name = import.Name;
        }
        if (TemplateId is > 0 && _templates.Any(template => template.Id == TemplateId.Value))
        {
            _selectedTemplateId = TemplateId;
            await OnTemplateSelected(TemplateId.Value);
        }
    }

    private void SelectInitialOwner()
    {
        if (ProjectId is > 0 && _projects.Any(project => project.Id == ProjectId.Value))
            _model.ProjectId = ProjectId;
        else if (EnvironmentId is > 0)
            _model.EnvironmentId = EnvironmentId;
        else if (ProjectServerId is > 0)
            _model.ProjectServerId = ProjectServerId;
    }

    // Publish the parent project so the NavMenu keeps the project's submenu open while we're editing
    // one of its pipelines, and (re)assert the breadcrumb. Called on every OnParametersSetAsync,
    // including same-component tab/back navigation, because BreadcrumbService clears on each
    // LocationChanged and would otherwise leave the crumb blank until the next full page load.
    private void ReassertNavContext()
    {
        ProjectNav.Set(_model.ProjectId);
        Breadcrumb.Set(PipelineEditBreadcrumbs.Build(L, _isNew, _pipeline, _model.ProjectId, _projects));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_suggestionsNeeded && _monacoEditor is not null)
        {
            _suggestionsNeeded = false;
            await LoadAutocompleteSuggestionsAsync(_model.ProjectId);
        }
    }

    private async Task OnSubmit()
    {
        // FRONT-008: Monaco's Ctrl+S and the Settings tab's Save call this outside the form's validity-gated
        // submit; the same rules gate every entry point.
        if (!_model.IsValid())
        {
            Toast.Error("ValidationError", "PipelineFormInvalid");
            return;
        }
        if (!_isNew && !await ConfirmSaveAsync()) return;

        _saving = true;
        // The confirmation dialog was the handler's first await, so the render it triggered predates the
        // save: without this one the button only turned busy once the server had answered.
        StateHasChanged();
        try
        {
            if (_isNew)
            {
                var outcome = await Api.Pipelines.CreatePipelineAsync(_model.ToCreateRequest());
                HandleSaveOutcome(outcome, "PipelineCreated", "Created",
                    created => Nav.NavigateTo($"/pipelines/{created.Id}"));
            }
            else
            {
                var submitted = _model.ToUpdateRequest() with { ConfirmedRepositoryYamlSha256 = _confirmedRepositoryYamlSha256 };
                _lastSubmitted = submitted;
                var outcome = await Api.Pipelines.UpdatePipelineAsync(Id!.Value, submitted);
                HandleSaveOutcome(outcome, "PipelineSaved", "Saved", updated =>
                {
                    _pipeline = updated;
                    // An unedited definition is replaced by the repository's own file: the page shows it.
                    _model.YamlDefinition = updated.YamlDefinition;
                    _lastSubmitted = submitted with { YamlDefinition = updated.YamlDefinition };
                    _savedYaml = _model.YamlDefinition;
                    _savedSourceBranch = _model.SourceBranch;
                    _ = RefreshSourceAsync();
                });
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task<bool> ConfirmSaveAsync()
    {
        var decision = await PipelineSaveDialog.ReviewAsync(
            Dialog, L["ReviewPipelineChange"].Value, Id ?? 0, _model, _savedYaml, _sourceBranches);
        if (decision is null) return false;
        _model.SourceBranch = decision.SourceBranch;
        _confirmedRepositoryYamlSha256 = decision.ConfirmedRepositoryYamlSha256;
        return true;
    }

    private async Task RefreshSourceAsync()
    {
        if (Id is not { } id) return;
        _source = await Api.Pipelines.GetPipelineSourceAsync(id);
        if (_source is { RepositoryId: > 0 })
            _sourceBranches = (await Api.Git.GetGitBranchesAsync(_source.RepositoryId)).Select(branch => branch.Name).ToList();
        await InvokeAsync(StateHasChanged);
    }

    // Surfaces the precise YAML validation errors (400) the API now returns on save instead of
    // a generic "save failed", keeping the contract identical to run/preflight.
    private void HandleSaveOutcome(
        ApiOutcome<PipelineDto, YamlValidationResultDto> outcome,
        string successKey, string successTitleKey, Action<PipelineDto> onSuccess)
    {
        if (outcome.Value is not null)
        {
            onSuccess(outcome.Value);
            Toast.Success(successTitleKey, successKey);
            return;
        }

        if (outcome.ErrorMessage is { } refusal)
            Toast.Notify(OmniSeverity.Danger, "Error", refusal);
        else
            Toast.Error("Error", "SaveFailed");
    }

    private void OnYamlValueChanged(string value)
    {
        _model.YamlDefinition = value;
        _yamlValid = null;
    }

    private async Task OnDebouncedYamlChanged(string value)
    {
        await OnValidateYaml();
    }

    private async Task OnFormatYaml()
    {
        if (_monacoEditor is not null)
        {
            await _monacoEditor.FormatDocumentAsync();
        }
    }

    // Ctrl+Click on a `pipeline: <name>` ref in the editor opens that pipeline's read-only definition peek.
    private void OnEditorPipelineRef(string name) => _peekName = name;

    private async Task OnValidateYaml()
    {
        if (string.IsNullOrWhiteSpace(_model.YamlDefinition)) return;

        _validating = true;
        StateHasChanged();

        _yamlValid = await YamlEditor.ValidateAsync(_model.YamlDefinition, _monacoEditor);

        _validating = false;
        StateHasChanged();
    }

    private async Task LoadAutocompleteSuggestionsAsync(int? projectId)
    {
        var data = await YamlEditor.LoadSuggestionsAsync(projectId, _monacoEditor);
        _serverNames = data.ServerNames;
        _libraryNames = data.LibraryNames;
        _vaultNames = data.VaultNames;
    }

    private void OnVisualYamlChanged(string yaml)
    {
        _model.YamlDefinition = yaml;
        _yamlValid = null;
    }

    private bool _running;

    private async Task OnRefresh()
    {
        if (_isNew || Id is null || _deleting) return;
        var (pipeline, forbidden) = await Api.Pipelines.LookUpPipelineAsync(Id.Value);
        _pipeline = pipeline;
        _changedElsewhere = false;
        if (_pipeline is null)
        {
            ShowNotFound(forbidden); // deleted elsewhere, or the access withdrawn, while this page was open
            return;
        }
        await RefreshRunsAsync();
        ApplyPipelineModel(_pipeline);
        await LoadBaseTemplateYamlAsync();
        await LoadFleetItemAsync();
        if (_liveRuns is not null) await _liveRuns.SyncGroupsAsync();
    }

    /// <summary>One page of the runs export, the same query as the grid's load.</summary>
    private Task<PaginatedResult<PipelineRunDto>> ReadRunsExportAsync(GridLoadArgs args)
    {
        if (Id is not { } pipelineId) return Task.FromResult(new PaginatedResult<PipelineRunDto>());
        var (page, pageSize) = args.ToPageRequest();
        return Api.Pipelines.GetPipelineRunsPagedAsync(pipelineId, page, pageSize, PipelineRunsGridQuery.From(args));
    }

    private async Task OnRunsLoadDataAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        _runsQuery = PipelineRunsGridQuery.From(args);
        await LoadRunsPageAsync(page, pageSize);
        if (_liveRuns is not null) await _liveRuns.SyncGroupsAsync();
    }

    private async Task LoadRunsPageAsync(int page, int pageSize)
    {
        if (Id is null) return;
        var pipelineId = Id.Value;
        var result = await Api.Pipelines.GetPipelineRunsPagedAsync(pipelineId, page, pageSize, _runsQuery);
        if (Id == pipelineId) ApplyRunsPage(result, page, pageSize);
    }

    /// <summary> A live update (a run started or moved, the definition changed elsewhere) refreshes
    /// the runs grid quietly, its page, sort, filters and scroll kept. Before the first run the
    /// grid is not rendered yet, so the page is fetched directly to make it appear. </summary>
    private Task RefreshRunsAsync() => _runsGrid is not null
        ? _runsGrid.Refresh()
        : LoadRunsPageAsync(_runsPage, _runsPageSize);

    /// <summary>A header filter that matches no run keeps the grid, so it can be cleared; the "no
    /// runs yet" text is only for a pipeline that has never run.</summary>
    private bool RunsFiltered => _runsQuery?.Filters is { Count: > 0 };

    private Func<string, string>? _runStatusText;
    private Func<string, string> RunStatusText => _runStatusText ??= GridFilterText.ForEnum<PipelineStatus>(L);

    private void ApplyRunsPage(PaginatedResult<PipelineRunDto> result, int page, int pageSize)
    {
        _runs = result.Items;
        if (page == 1) _firstRunsPage = result.Items;
        _runsTotalCount = result.TotalCount;
        _runsPage = page;
        _runsPageSize = pageSize;
    }

    private async Task EnsureHubAsync()
    {
        if (_liveRuns is not null || Id is null) return;
        _liveRuns = new PipelineRunsLiveConnection(
            HubFactory,
            Logger,
            Id.Value,
            () => _runs.Where(run => run.Status is PipelineStatus.Running or PipelineStatus.Pending)
                .Select(run => run.Id).ToArray(),
            ReloadRunsAsync,
            InvokeAsync);
        await _liveRuns.StartAsync();
        // Changed elsewhere: reloaded when nothing is being edited, otherwise a banner (never overwritten).
        _definitionSync = new PipelineDefinitionLiveSync(HubFactory, Id.Value, () => InvokeAsync(OnDefinitionChangedAsync));
        await _definitionSync.StartAsync();
    }

    // SITE-FUNC-SELF-SAVE-001: with unsaved edits, the stored pipeline decides; the page's own save
    // raises no banner, any other stored content does.
    private async Task OnDefinitionChangedAsync()
    {
        if (!PipelineDefinitionLiveSync.HasUnsavedChanges(_model, _pipeline, _savedSourceBranch)) await OnRefresh();
        else if (!PipelineDefinitionLiveSync.IsOwnSave(_lastSubmitted, await Api.Pipelines.GetPipelineAsync(Id!.Value)))
            _changedElsewhere = true;
        StateHasChanged();
    }

    private async Task ReloadRunsAsync()
    {
        if (Id is null || _deleting) return;
        var (pipeline, forbidden) = await Api.Pipelines.LookUpPipelineAsync(Id.Value);
        if (pipeline is null)
        {
            ShowNotFound(forbidden); // deleted elsewhere, or the access withdrawn: the runs go with it
            StateHasChanged();
            return;
        }
        await RefreshRunsAsync();
        _pipeline = pipeline;
        if (_liveRuns is not null) await _liveRuns.SyncGroupsAsync();
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        if (_liveRuns is not null) await _liveRuns.DisposeAsync();
        if (_definitionSync is not null) await _definitionSync.DisposeAsync();
    }
    /// <summary>Cancels the run from the run table. Reuses the run view's coordinator so the confirm
    /// wording, the toasts and the forbidden case behave identically wherever a run is cancelled.</summary>
    private async Task OnCancelRunAsync(PipelineRunDto run)
    {
        var coordinator = new PipelineRunCommandCoordinator(Api, Dialog, Toast, L, Js, Veil);
        if (!await coordinator.ConfirmCancellationAsync()) return;
        await coordinator.CancelAsync(run);
        await ReloadRunsAsync();
    }

    private Task OnRun() => OnRunAsync(null);
    private async Task OnRunWithOptionsAsync()
    {
        var choice = await RunDialogs.ConfigureLaunchAsync(Id!.Value);
        if (choice is null) return;
        await OnConfiguredRunAsync(choice);
    }

    private Task OnRunAsync(string? sourceBranch) =>
        RunAsync(launcher => launcher.LaunchAsync(Id!.Value, sourceBranch));

    private Task OnConfiguredRunAsync(PipelineLaunchChoice choice) =>
        RunAsync(launcher => launcher.LaunchAsync(Id!.Value, choice));

    private async Task RunAsync(Func<PipelineRunLauncher, Task<PipelineRunLaunchResult?>> launch)
    {
        if (_running) return;
        _running = true;
        try
        {
            // The launch is busy on the Run button only, and the run appears in this page grid.
            var launcher = new PipelineRunLauncher(Api, RunGate, RunDialogs, Nav, Toast, L) { OpenLaunchedRun = false };
            if (await launch(launcher) is not null) await ReloadRunsAsync();
        }
        finally { _running = false; }
    }
    private async Task OnDryRun()
    {
        var result = await Api.Pipelines.DryRunPipelineAsync(Id!.Value);
        if (result is null) return;
        await Dialog.OpenAsync<DryRunResultDialog>(L["DryRunResult"].Value,
            new Dictionary<string, object?> { { "Result", result } },
            new OmniDialogOptions { Width = "800px", Height = "600px", AutoFocusFirstElement = false });
    }
    private async Task OnDelete()
    {
        var confirmed = await Dialog.Confirm(L["DeletePipelineConfirm"].Value, L["DeletePipeline"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true || _deleting) return;
        // R-34: the live connections stop before the pipeline goes. Deleting it raises events for its
        // runs, and each one reloaded this page from a pipeline that no longer exists, leaving the page
        // on a deleted pipeline instead of going back to the list.
        _deleting = true;
        // R-98: the veil shows before the deletion runs, so the page never looks idle while it does.
        StateHasChanged();
        await StopLiveUpdatesAsync();
        var status = await Api.Pipelines.DeletePipelineAsync(Id!.Value);
        if (!status.Success && !status.NotFound)
        {
            _deleting = false;
            Toast.Error("Error", "DeleteFailed");
            await EnsureHubAsync();
            return;
        }
        Cache.InvalidatePrefix("pipelines:");
        PageState.Forget<PipelinePageState>($"pipelines/{Id}");
        Toast.Success("Deleted", "Deleted");
        Nav.NavigateTo(PipelineRunPresentation.DeletedPipelineHref(ProjectId, ServerId, ReturnUrl));
    }
}
