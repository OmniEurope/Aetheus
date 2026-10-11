// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineEdit : IAsyncDisposable
{
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
    [SupplyParameterFromQuery] public int? EnvironmentId { get; set; }
    [SupplyParameterFromQuery] public int? ProjectServerId { get; set; }
    [SupplyParameterFromQuery(Name = "templateId")] public int? TemplateId { get; set; }

    private PipelineDto? _pipeline;
    private PipelineDefinitionLiveSync? _definitionSync;
    private bool _changedElsewhere;
    private UpdatePipelineRequest? _lastSubmitted;
    private PipelineSourceDto? _source;
    private List<string> _sourceBranches = [];
    private PipelineModel _model = new();
    private string? _savedSourceBranch;
    private List<PipelineRunDto> _runs = [];
    private Aetheus.Front.Components.Shared.AetheusDataGrid<PipelineRunDto>? _runsGrid;
    private int _runsTotalCount;
    private int _runsPage = 1;
    private int _runsPageSize = 25;

    /// <summary>Runs tab tiles, from the first block of runs: the grid scrolls virtually (R-327).</summary>
    private PipelineRunsSummary RunsSummary => PipelineRunsSummary.Create(_firstRunsPage);

    /// <summary>Recette R2-128: a summary tile that opens a run draws as the other clickable tiles.</summary>
    private static string SummaryCardClass(int? runId) => runId is null
        ? "analysis-summary-card pipeline-runs-summary-card"
        : "analysis-summary-card pipeline-runs-summary-card tile-clickable";

    private List<PipelineRunDto> _firstRunsPage = [];

    /// <summary>Sort and column filters currently applied to the runs grid. Held across reloads so a
    /// refresh, a live update or a launch re-fetches the page the reader is actually looking at.</summary>
    private PipelineRunPaginationRequest? _runsQuery;

    private PipelineRunsLiveConnection? _liveRuns;
    private List<ProjectDto> _projects = [];
    private bool _isNew => Id is null or 0;
    private bool _saving;
    private bool _loading;
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

    private async Task ResetEditorStateAsync()
    {
        if (_liveRuns is not null) await _liveRuns.DisposeAsync();
        if (_definitionSync is not null) await _definitionSync.DisposeAsync();
        _liveRuns = null;
        _definitionSync = null;
        _changedElsewhere = false;
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
        // Recette R2-087: a return shows at once what the page last showed, then refreshes behind it;
        // the page-wide loader is for a first visit only.
        var address = $"pipelines/{Id}";
        var ticket = PageState.Begin();
        var restored = PageState.TryRestore<PipelinePageState>(address, out var state);
        if (restored) ApplyPageState(state);
        _loading = !restored;
        var pipelineTask = Api.Pipelines.GetPipelineAsync(Id!.Value);
        var runsTask = Api.Pipelines.GetPipelineRunsPagedAsync(Id.Value, 1, _runsPageSize);
        var projectsTask = Api.Projects.GetAllProjectsAsync();
        var sourceTask = Api.Pipelines.GetPipelineSourceAsync(Id.Value);
        var templatesTask = LoadTemplatesSafeAsync();
        await Task.WhenAll(pipelineTask, runsTask, projectsTask, sourceTask, templatesTask);
        if (generation != _loadGeneration) return;
        // What the reader typed or filtered while the refresh ran stays theirs.
        var editorUntouched = !restored || string.Equals(_model.YamlDefinition, _savedYaml, StringComparison.Ordinal);
        _pipeline = await pipelineTask;
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

    /// <summary>What the pipeline page shows, kept for a return to it (recette R2-087).</summary>
    private sealed record PipelinePageState(
        PipelineDto Pipeline, List<PipelineRunDto> FirstRuns, int RunsTotalCount, List<ProjectDto> Projects,
        List<PipelineTemplateSummaryDto> Templates, PipelineSourceDto? Source, List<string> SourceBranches,
        string? BaseTemplateYaml, PipelineFleetItemDto? FleetItem);

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
            ProjectServerId = pipeline.ProjectServerId
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
                _lastSubmitted = _model.ToUpdateRequest();
                var outcome = await Api.Pipelines.UpdatePipelineAsync(Id!.Value, _lastSubmitted);
                HandleSaveOutcome(outcome, "PipelineSaved", "Saved", updated =>
                {
                    _pipeline = updated;
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
        var result = await Dialog.OpenAsync<PipelineSaveDialog>(
            L["ReviewPipelineChange"].Value,
            new Dictionary<string, object?>
            {
                { "SavedYaml", _savedYaml },
                { "DraftYaml", _model.YamlDefinition },
                { "Branches", _sourceBranches },
                { "SourceBranch", _model.SourceBranch }
            },
            new OmniDialogOptions { Width = "900px", Height = "720px", AutoFocusFirstElement = false });

        if (result is not PipelineSaveDecision decision) return false;
        _model.SourceBranch = decision.SourceBranch;
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
        if (_isNew || Id is null) return;
        await RefreshRunsAsync();
        _pipeline = await Api.Pipelines.GetPipelineAsync(Id.Value);
        _changedElsewhere = false;
        if (_pipeline is not null)
        {
            ApplyPipelineModel(_pipeline);
            await LoadBaseTemplateYamlAsync();
            await LoadFleetItemAsync();
        }
        if (_liveRuns is not null) await _liveRuns.SyncGroupsAsync();
    }

    /// <summary>Recette R2-124: one page of the runs export, the same query as the grid's load.</summary>
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

    /// <summary>
    /// Recette R-226: a live update (a run started or moved, the definition changed elsewhere) refreshes
    /// the runs grid quietly, its page, sort, filters and scroll kept. Before the first run the grid is
    /// not rendered yet, so the page is fetched directly to make it appear.
    /// </summary>
    private Task RefreshRunsAsync() => _runsGrid is not null
        ? _runsGrid.Refresh()
        : LoadRunsPageAsync(_runsPage, _runsPageSize);

    /// <summary>Recette R-210: a header filter that matches no run keeps the grid, so it can be cleared;
    /// the "no runs yet" text is only for a pipeline that has never run.</summary>
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

    private Task OnPipelineRunStarted(int runId, int pipelineId) => pipelineId == Id
        ? ReloadRunsAsync()
        : Task.CompletedTask;

    private async Task ReloadRunsAsync()
    {
        if (Id is null) return;
        await RefreshRunsAsync();
        _pipeline = await Api.Pipelines.GetPipelineAsync(Id.Value);
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
        var coordinator = new PipelineRunCommandCoordinator(Api, Dialog, Toast, L, Js);
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
            // R2-038: the launch is busy on the Run button only, and the run appears in this page grid.
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
        if (confirmed != true) return;
        var status = await Api.Pipelines.DeletePipelineAsync(Id!.Value);
        if (!status.Success) { Toast.Error("Error", "DeleteFailed"); return; }
        Cache.InvalidatePrefix("pipelines:");
        PageState.Forget<PipelinePageState>($"pipelines/{Id}");
        Toast.Success("Deleted", "Deleted");
        Nav.NavigateTo("/pipelines");
    }

    // Recette R2-124: the export writes the duration and the server the cells show.
    private static readonly Func<PipelineRunDto, object?> RunDurationExport = run => PipelineRunFormatting.FormatDuration(run.StartedAt, run.CompletedAt);
    private static readonly Func<PipelineRunDto, object?> RunServerExport = run =>
        run.Steps.FirstOrDefault(step => !step.IsSystem && step.ServerName is not null)?.ServerName ?? string.Empty;
}
