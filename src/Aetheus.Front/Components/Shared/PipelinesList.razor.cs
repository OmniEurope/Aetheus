// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Microsoft.AspNetCore.Components.Forms;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Shared pipeline overview. Global and project scopes use the dependency graph so parents,
/// children and inverse parent links stay coherent; the server scope retains the bounded page.
/// </summary>
public partial class PipelinesList : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private PipelineRunGate RunGate { get; set; } = default!;
    [Inject] private PipelineRunDialogCoordinator RunDialogs { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private PipelineImportState ImportState { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ILogger<PipelinesList> Logger { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private PageStateCache PageState { get; set; } = default!;

    /// <summary>Name of the page-header outlet the list fills with its actions (recette R-124): the list has no toolbar row of its own. The
    /// three hosts pass it to their <see cref="PageHeader"/> as <c>ActionsSection</c>.</summary>
    internal const string HeaderActionsSection = "pipelines-header-actions";

    /// <summary>Project filter (project detail scope). Mutually exclusive with <see cref="ServerId"/>.</summary>
    [Parameter] public int? ProjectId { get; set; }

    /// <summary>Server filter (server detail scope). When both are null the list is global (unfiltered).</summary>
    [Parameter] public int? ServerId { get; set; }

    /// <summary>Optional model filter supplied by the unified global pipelines route.</summary>
    [Parameter] public int? TemplateId { get; set; }

    private bool IsServerScope => ServerId.HasValue;
    private bool IsProjectScope => ProjectId.HasValue;
    private bool IsGlobal => !IsServerScope && !IsProjectScope;

    // Recette R-121: the "with children" and "without children" groups are one list now.
    private List<PipelineDependencyDto> _pipelines = [];
    private List<PipelineRunDto> _recentRuns = [];
    private Dictionary<int, PipelineFleetItemDto> _fleetItems = [];
    private List<PipelineTemplateSummaryDto> _templateOptions = [];
    private HashSet<int> _favoritePipelineIds = [];
    private readonly HashSet<int> _pendingFavoritePipelineIds = [];
    private int _totalCount;
    private bool _loading;
    private bool _canWrite;
    private bool _favoritesOnly;
    private bool _withChildrenOnly;
    // Recette R-167: every filter takes one value or several.
    private readonly HashSet<PipelineCatalogStatus> _statusFilters = [];

    // Recette R-120 / R-215: the Catalogue and Runs views (PipelineListViews).
    private PipelineListViews? _views;
    private PipelineListViews Views => _views ??= new PipelineListViews(Js, Logger, () => $"aetheus.pipelines.view-tab.{Auth.Username ?? "anonymous"}");

    /// <summary>The view named in the URL (<c>?tab=catalog|runs</c>). A slug of an enclosing tab set
    /// (the pipelines hub's <c>?tab=used</c>) names no view here and leaves the current one.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "tab")] public string? Tab { get; set; }

    // Global scope only: search + trigger filter + import.
    private string? _search;
    private IReadOnlyList<PipelineTriggerType> _triggerFilters = [];
    private IReadOnlyList<int> _templateFilterIds = [];
    private int? _templateParameterSeen;
    private bool _fleetAvailable;
    private List<OmniOption<PipelineTriggerType>> _triggerOptions = [];
    private static readonly IReadOnlyList<string> YamlContentTypes =
        ["application/x-yaml", "application/yaml", "text/yaml", "text/x-yaml", "text/plain"];
    private bool _showPipelineImporter;
    private HubConnection? _hubConnection;
    private HubConnection? _entityHub;
    private readonly HashSet<int> _joinedRunGroups = [];
    // #7: the pipelines hub broadcasts every run across the org (no project/server context to filter on),
    // so coalesce a burst of (mostly unrelated) events into a single trailing refresh.
    private const int LiveRefreshDebounceMs = 1000;
    private readonly TrailingReloadCoalescer _liveCoalescer = new(LiveRefreshDebounceMs);
    private readonly CancellationTokenSource _liveLifetime = new();
    private Task? _liveRecoveryTask;

    protected override void OnParametersSet()
    {
        Views.ApplyTabParameter(Tab);

        // The ?templateId= link (fleet page) preselects that model once; the choice is the user's after.
        if (_templateParameterSeen == TemplateId) return;
        _templateParameterSeen = TemplateId;
        _templateFilterIds = TemplateId is { } templateId ? [templateId] : [];
    }

    protected override async Task OnInitializedAsync()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();

        _triggerOptions = [.. Enum.GetValues<PipelineTriggerType>().Select(trigger => new OmniOption<PipelineTriggerType>(trigger, L.Localize(trigger)))];

        // The tabs render once the overview is in, so the remembered tab is known before they do.
        // Recette R2-087: a return shows the list as it was left, filters included, and refreshes it.
        _stateTicket = PageState.Begin();
        _overviewLoaded = RestoreState();
        _loading = !_overviewLoaded;
        await Views.RestoreAsync();
        await LoadOverviewAsync();
        RememberState();

        await StartHubAsync();
    }

    private IReadOnlyList<OmniOption<string>> ViewOptions =>
    [
        new(PipelineListViews.CatalogSlug, $"{L["PipelineCatalogTab"]} ({FilteredCatalog.Count})"),
        new(PipelineListViews.RunsSlug, $"{L["Runs"]} ({PipelineRunTableItem.GroupRuns(_recentRuns).Count})"),
    ];

    private Task OnViewChangedAsync(string slug) => Views.SelectAsync(slug, Nav);

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.Pipeline);

    /// <summary>The catalogue as the table shows it: every filter applied, starred pipelines first
    /// (recette R-119).</summary>
    private IReadOnlyList<PipelineDependencyDto> FilteredCatalog => PipelineCatalogView.DefaultOrder(
        FilteredBeforeStatus.Where(item => PipelineCatalogView.MatchesAny(item, _statusFilters, _fleetItems)),
        _favoritePipelineIds);

    /// <summary>Every filter but the status chip: what each chip counts, so a chip's number is the row
    /// count its click gives.</summary>
    private IEnumerable<PipelineDependencyDto> FilteredBeforeStatus => ScopeFilter(_pipelines)
        .Where(item => !_favoritesOnly || _favoritePipelineIds.Contains(item.Id))
        .Where(item => !_withChildrenOnly || PipelineDependencyGrid.HasChildren(item))
        .Where(MatchesTemplateFilter)
        .Where(item => string.IsNullOrWhiteSpace(_search)
            || item.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || (item.ProjectName?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false))
        .Where(item => _triggerFilters.Count == 0 || _triggerFilters.Contains(item.TriggerType));

    private IEnumerable<PipelineDependencyDto> ScopeFilter(IEnumerable<PipelineDependencyDto> source) => source
        .Where(item => !ProjectId.HasValue || item.ProjectId == ProjectId);

    private bool HasActiveFilters => _favoritesOnly || _withChildrenOnly || _statusFilters.Count > 0
        || _triggerFilters.Count > 0 || _templateFilterIds.Count > 0 || !string.IsNullOrEmpty(_search);

    private List<OmniOption<int>> TemplateFilterOptions =>
        [.. _templateOptions.Select(template => new OmniOption<int>(template.Id, template.Name))];

    private int StatusCount(PipelineCatalogStatus status) =>
        PipelineCatalogView.Count(FilteredBeforeStatus, status, _fleetItems);

    // The summary scrolls on a phone, and OE's scrolling OmniStack leaves its viewport out of the tab
    // order: with every chip disabled the region holds nothing a keyboard can reach (axe
    // scrollable-region-focusable) and nothing to act on, so it is not rendered. Workaround until OE
    // makes that viewport focusable.
    private bool HasActionableStatusChip => _statusFilters.Count > 0
        || PipelineCatalogView.Statuses(_fleetAvailable).Any(status => StatusCount(status) > 0);

    private void ToggleStatusFilter(PipelineCatalogStatus status)
    {
        if (!_statusFilters.Remove(status))
            _statusFilters.Add(status);
    }

    private string StatusLabel(PipelineCatalogStatus status) => L[PipelineCatalogView.LabelKey(status)];

    /// <summary>Set once the overview has been shown; later loads refresh it in place.</summary>
    private bool _overviewLoaded;

    private async Task LoadOverviewAsync()
    {
        // R-10: the page-wide loader is for a page with nothing to show yet. A launch, a run event or
        // the refresh button reload in place: pressing Run used to replace the whole page by the
        // loader, over the run it had just added.
        _loading = !_overviewLoaded;
        try
        {
            var graphTask = Api.Pipelines.GetPipelineDependencyGroupsAsync(ServerId, ProjectId);
            var recentTask = Api.Pipelines.GetRecentPipelineRunsAsync(ProjectId, ServerId);
            var favoritesTask = Api.Pipelines.GetPipelineFavoritesAsync();
            var fleetTask = LoadFleetItemsAsync();
            await Task.WhenAll(graphTask, recentTask, favoritesTask, fleetTask);
            var graph = await graphTask;
            _pipelines = graph.Parents.Concat(graph.Leaves).DistinctBy(item => item.Id).ToList();
            _favoritePipelineIds = (await favoritesTask).PipelineIds.ToHashSet();
            _totalCount = ScopeFilter(_pipelines).Count();
            _recentRuns = (await recentTask)
                .Take(20)
                .ToList();
            await SyncRunGroupsAsync();
            _overviewLoaded = true;
        }
        finally { _loading = false; }
    }

    private async Task LoadFleetItemsAsync()
    {
        try
        {
            // PLAN-003 lot 12: this used to page through the WHOLE fleet - every pipeline of every
            // project, MaxPageSize at a time - and then keep only the rows of the project being
            // looked at. The endpoint has always accepted a projectId; the page simply never sent it.
            var items = new List<PipelineFleetItemDto>();
            var page = 1;
            while (true)
            {
                var result = await Api.Pipelines.GetPipelineFleetAsync(
                    page, PaginationRequest.MaxPageSize, projectId: ProjectId);
                items.AddRange(result.Items);
                if (items.Count >= result.TotalCount || result.Items.Count == 0)
                    break;
                page++;
            }

            _fleetItems = items
                .DistinctBy(item => item.PipelineId)
                .ToDictionary(item => item.PipelineId);
            _templateOptions = items
                .Where(item => item.TemplateId.HasValue && !string.IsNullOrWhiteSpace(item.TemplateName))
                .DistinctBy(item => item.TemplateId)
                .OrderBy(item => item.TemplateName, StringComparer.OrdinalIgnoreCase)
                .Select(item => new PipelineTemplateSummaryDto
                {
                    Id = item.TemplateId!.Value,
                    Name = item.TemplateName!
                })
                .ToList();
            if (items.Any(item => item.TemplateId is null && string.IsNullOrWhiteSpace(item.TemplateName)))
                _templateOptions.Insert(0, new PipelineTemplateSummaryDto
                {
                    Id = -1,
                    Name = L["AutonomousPipeline"]
                });
            _fleetAvailable = true;
        }
        catch (HttpRequestException)
        {
            _fleetItems = [];
            _templateOptions = [];
            _fleetAvailable = false;
        }
    }

    // A launched run opens its own page, in the SPA, instead of reloading the whole overview behind it.
    private async Task RunPipelineClick(int id, string? mode)
    {
        var launcher = new PipelineRunLauncher(Api, RunGate, RunDialogs, Nav, Toast, L);
        if (mode != "options")
        {
            await launcher.LaunchAsync(id, sourceBranch: null);
            return;
        }

        var choice = await RunDialogs.ConfigureLaunchAsync(id);
        if (choice is null) return;
        await launcher.LaunchAsync(id, choice);
    }

    private Task ReloadGrid() => LoadOverviewAsync();

    private async Task SetFavoriteAsync(int pipelineId, bool isFavorite)
    {
        if (!_pendingFavoritePipelineIds.Add(pipelineId)) return;
        await InvokeAsync(StateHasChanged);
        try
        {
            var favorite = await Api.Pipelines.SetPipelineFavoriteAsync(pipelineId, isFavorite);
            if (favorite is null)
            {
                Toast.Error("Error", "FavoriteUpdateFailed");
                return;
            }

            if (favorite.IsFavorite)
                _favoritePipelineIds.Add(pipelineId);
            else
                _favoritePipelineIds.Remove(pipelineId);
            Toast.Success(
                favorite.IsFavorite ? "AddedToFavorites" : "RemovedFromFavorites",
                favorite.IsFavorite ? "AddedToFavorites" : "RemovedFromFavorites");
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "FavoriteUpdateFailed");
        }
        finally
        {
            _pendingFavoritePipelineIds.Remove(pipelineId);
            await InvokeAsync(StateHasChanged);
        }
    }

    private string StateAddress => $"pipelines-list:{ServerId}:{ProjectId}";

    private bool RestoreState()
    {
        if (!PageState.TryRestore<PipelinesListState>(StateAddress, out var state)) return false;
        _pipelines = state.Pipelines;
        _recentRuns = state.RecentRuns;
        _fleetItems = state.FleetItems;
        _templateOptions = state.TemplateOptions;
        _fleetAvailable = state.FleetAvailable;
        _favoritePipelineIds = state.FavoritePipelineIds;
        _totalCount = state.TotalCount;
        _search = state.Search;
        _triggerFilters = state.TriggerFilters;
        // A ?templateId= link chooses its model; otherwise the reader's last choice comes back.
        if (TemplateId is null) _templateFilterIds = state.TemplateFilterIds;
        _statusFilters.UnionWith(state.StatusFilters);
        _favoritesOnly = state.FavoritesOnly;
        _withChildrenOnly = state.WithChildrenOnly;
        return true;
    }

    // FRONT-020: the session this page was opened in; once signed out, its state is not remembered.
    private ListCacheService.Ticket _stateTicket;

    private void RememberState()
    {
        if (!_overviewLoaded) return;
        PageState.Remember(_stateTicket, StateAddress, new PipelinesListState(_pipelines, _recentRuns, _fleetItems, _templateOptions,
            _fleetAvailable, _favoritePipelineIds, _totalCount, _search, _triggerFilters, _templateFilterIds,
            [.. _statusFilters], _favoritesOnly, _withChildrenOnly));
    }

    private Task ApplyFilters()
    {
        return InvokeAsync(StateHasChanged);
    }

    private Task OnTemplateFilterChanged(IReadOnlyList<int> values)
    {
        _templateFilterIds = values;
        return ApplyFilters();
    }

    private bool MatchesTemplateFilter(PipelineDependencyDto pipeline)
    {
        if (_templateFilterIds.Count == 0) return true;
        if (!_fleetItems.TryGetValue(pipeline.Id, out var fleetItem)) return false;
        return _templateFilterIds.Any(templateId => templateId == -1
            ? fleetItem.TemplateId is null && string.IsNullOrWhiteSpace(fleetItem.TemplateName)
            : fleetItem.TemplateId == templateId);
    }

    private string PipelineHref(int id) => IsProjectScope
        ? $"/pipelines/{id}?projectId={ProjectId}"
        : IsServerScope ? $"/pipelines/{id}?serverId={ServerId}" : $"/pipelines/{id}";


    private Task OnPipelineHubEvent(int pipelineId)
    {
        Logger.LogInformation("Pipeline live event received for pipeline {PipelineId}", pipelineId);
        return InvokeAsync(() => _liveCoalescer.RequestAsync(() => InvokeAsync(RefreshLiveAsync)));
    }

    private async Task OnPipelineRunEvent(int runId)
    {
        try
        {
            var run = await Api.Pipelines.GetPipelineRunAsync(runId);
            if (run is not null) await OnPipelineHubEvent(run.PipelineId);
        }
        catch (HttpRequestException) { } // hub-triggered reload - silent on auth failure
    }

    // Hub-driven refresh. Global scope is server-paginated, so re-paging re-fetches; the client
    // scopes hold the full list in memory, so they must re-fetch it first to pick up new run state.
    private async Task RefreshLiveAsync()
    {
        await ReloadGrid();
        await InvokeAsync(StateHasChanged);
    }

    // --- Global scope: filters + create/import (toolbar) ---

    private async Task ClearFilters()
    {
        _search = null;
        _triggerFilters = [];
        _templateFilterIds = [];
        _favoritesOnly = false;
        _withChildrenOnly = false;
        _statusFilters.Clear();
        await ApplyFilters();
    }

    private void NewPipeline()
    {
        var href = IsProjectScope
            ? $"/pipelines/setup?projectId={ProjectId}"
            : IsServerScope
                ? $"/pipelines/new?serverId={ServerId}"
                : "/pipelines/setup";
        Nav.NavigateTo(href);
    }

    // S-FEAT-17: import a pipeline from an uploaded YAML file - read + validate the name, then stash
    // it for the new-pipeline form (which collects the required owner before saving).
    private void OnImportPipelineClick() => _showPipelineImporter = !_showPipelineImporter;

    private async Task OnPipelineFileSelected(IReadOnlyList<IBrowserFile> files)
    {
        var file = files.Single();
        if (file.Size == 0 || file.Size > 50_000) return;

        using var reader = new StreamReader(file.OpenReadStream(50_000));
        var yaml = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(yaml)) return;

        string? name = null;
        try
        {
            var def = await Api.Packages.ValidatePipelineYamlAsync(yaml);
            name = def?.Name;
        }
        catch (HttpRequestException) { /* fall back to a blank name */ }

        ImportState.Set(yaml, name);
        _showPipelineImporter = false;
        Nav.NavigateTo("/pipelines/new");
    }

    // --- Global scope: live refresh via the pipelines + entities hubs ---

    private async Task StartHubAsync()
    {
        // Recette R2-100: the page can be left while its overview loads. DisposeAsync then cancels and
        // disposes the live lifetime, and reading its Token here threw ObjectDisposedException into the
        // error boundary. Cancellation always precedes disposal, so IsCancellationRequested (which never
        // throws) says the page is gone; the token is read once, while the source is alive.
        if (_liveLifetime.IsCancellationRequested) return;
        var lifetime = _liveLifetime.Token;
        try
        {
            _hubConnection = HubFactory.Create("pipelines");
            _hubConnection.On<int, int>("PipelineRunStarted", (_, pipelineId) => OnPipelineHubEvent(pipelineId));
            _hubConnection.On<int, PipelineStatus>("PipelineRunCompleted", (runId, _) => OnPipelineRunEvent(runId));
            _hubConnection.On<int>("PipelineRunCancelled", runId => OnPipelineRunEvent(runId));
            _hubConnection.On<int?>("StepStarted", _ => OnPipelineProgressEvent());
            _hubConnection.On<int?, TaskExecutionStatus>("StepCompleted", (_, _) => OnPipelineProgressEvent());
            _hubConnection.On<int, string, string>("ApprovalRequired", (_, _, _) => OnPipelineProgressEvent());
            _hubConnection.On<int, string, string>("ApprovalResolved", (_, _, _) => OnPipelineProgressEvent());
            // Group membership is per-connection and lost on auto-reconnect - re-join + reload.
            _hubConnection.RejoinOnReconnect(async () =>
            {
                await _hubConnection.InvokeAsync("JoinPipelineUpdatesGroup");
                _joinedRunGroups.Clear();
                await SyncRunGroupsAsync();
                await RefreshLiveAsync();
            });
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinPipelineUpdatesGroup");
            await SyncRunGroupsAsync();
            Logger.LogInformation("Pipeline live connection subscribed");
        }
        catch (Exception exception)
        {
            Logger.LogDebug(exception, "Pipeline live connection was unavailable during initial startup");
        }

        // Left while the first hub started: DisposeAsync has run, a hub created now would never be disposed.
        if (lifetime.IsCancellationRequested) return;
        try
        {
            _entityHub = HubFactory.Create("entities");
            _entityHub.On<ResourceType, int, string>("EntityChanged", (type, id, _) =>
                type == ResourceType.Pipeline ? OnPipelineHubEvent(id) : Task.CompletedTask);
            _entityHub.RejoinOnReconnect(async () =>
            {
                await _entityHub.InvokeAsync("JoinEntityUpdates", ResourceType.Pipeline);
                await RefreshLiveAsync();
            });
            await _entityHub.StartAsync();
            await _entityHub.InvokeAsync("JoinEntityUpdates", ResourceType.Pipeline);
            Logger.LogInformation("Pipeline entity live connection subscribed");
        }
        catch (Exception exception)
        {
            Logger.LogDebug(exception, "Pipeline entity live connection was unavailable during initial startup");
        }

        if (lifetime.IsCancellationRequested) return;
        _liveRecoveryTask = RecoverLiveConnectionsAsync(lifetime);
    }

    private async Task RecoverLiveConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                var recovered = false;
                if (_hubConnection is { State: HubConnectionState.Disconnected })
                {
                    await _hubConnection.StartAsync(ct);
                    await _hubConnection.InvokeAsync("JoinPipelineUpdatesGroup", ct);
                    _joinedRunGroups.Clear();
                    await SyncRunGroupsAsync();
                    recovered = true;
                }

                if (_entityHub is { State: HubConnectionState.Disconnected })
                {
                    await _entityHub.StartAsync(ct);
                    await _entityHub.InvokeAsync("JoinEntityUpdates", ResourceType.Pipeline, ct);
                    recovered = true;
                }

                if (recovered)
                    await InvokeAsync(RefreshLiveAsync);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Logger.LogDebug(exception, "Pipeline live connection recovery will retry");
            }
        }
    }

    private Task OnPipelineProgressEvent() =>
        InvokeAsync(() => _liveCoalescer.RequestAsync(() => InvokeAsync(RefreshLiveAsync)));

    private async Task SyncRunGroupsAsync()
    {
        if (_hubConnection?.State != HubConnectionState.Connected) return;

        foreach (var run in _recentRuns.Where(run => run.Status is PipelineStatus.Running
                     or PipelineStatus.Pending or PipelineStatus.WaitingForApproval))
        {
            if (!_joinedRunGroups.Add(run.Id)) continue;
            try { await _hubConnection.InvokeAsync("JoinPipelineRunGroup", run.Id); }
            catch { _joinedRunGroups.Remove(run.Id); }
        }
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        // Disposed once: a second call would Cancel() the already disposed live lifetime.
        if (_disposed) return;
        _disposed = true;
        RememberState();
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        _liveLifetime.Cancel();
        if (_liveRecoveryTask is not null)
        {
            try { await _liveRecoveryTask; }
            catch (OperationCanceledException) { }
        }
        if (_hubConnection is not null)
        {
            try { await _hubConnection.InvokeAsync("LeavePipelineUpdatesGroup"); } catch { /* best-effort */ }
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }
        if (_entityHub is not null)
        {
            try { await _entityHub.InvokeAsync("LeaveEntityUpdates", ResourceType.Pipeline); } catch { /* best-effort */ }
            await _entityHub.DisposeAsync();
            _entityHub = null;
        }
        _liveLifetime.Dispose();
    }
}
