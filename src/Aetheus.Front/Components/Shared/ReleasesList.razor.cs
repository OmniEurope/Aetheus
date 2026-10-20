// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
namespace Aetheus.Front.Components.Shared;

/// <summary>
/// 0-a: single shared Releases list, reused across the 3 scopes (global <c>/releases</c>, project
/// detail, server detail) instead of three diverged copies. Filter is driven by the optional
/// <see cref="ProjectId"/> / <see cref="ServerId"/> parameters:
/// <list type="bullet">
/// <item>both null = global, server-paginated, with a project filter dropdown;</item>
/// <item><see cref="ProjectId"/> set = project scope, server-paginated, Project column hidden;</item>
/// <item><see cref="ServerId"/> set = server scope, self-loaded via <c>GetServerReleasesAsync</c>
/// (no server-side serverId paging exists yet) and paged in-memory.</item>
/// </list>
/// Self-loading (the section fetches its own data) - no dependency on the detail loaders. A
/// stale-while-revalidate <see cref="ListCacheService"/> entry renders the last list instantly on
/// revisit while a fresh fetch runs behind.
/// </summary>
public partial class ReleasesList : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;
    [Inject] private ProjectDetailLoader ProjectLoader { get; set; } = default!;
    [Inject] private PipelineRunGate RunGate { get; set; } = default!;
    [Inject] private PipelineRunDialogCoordinator RunDialogs { get; set; } = default!;

    /// <summary>Project filter (project detail scope). Mutually exclusive with <see cref="ServerId"/>.</summary>
    [Parameter] public int? ProjectId { get; set; }

    /// <summary>Server filter (server detail scope). Mutually exclusive with <see cref="ProjectId"/>.</summary>
    [Parameter] public int? ServerId { get; set; }

    private bool IsServerScope => ServerId.HasValue;
    private bool IsProjectScope => ProjectId.HasValue;
    private bool IsGlobal => !ProjectId.HasValue && !ServerId.HasValue;

    private HubConnection? _hubConnection;
    private List<ReleaseDto> _releases = [];

    private List<ProjectDto> _projects = [];
    private int _totalCount;
    private bool _loading;
    private bool _syncing;
    private bool _canWrite;
    private bool _canAdminProject;
    private int? _projectFilter; // Offer project choices only in global scope and send column filters to the API so the
    // displayed releases and filter options share the list's scope.
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private ReleaseFilterValuesDto _filterValues = new();
    private Func<string, string>? _statusText;
    private Func<string, string>? _gradeText;
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<ReleaseStatus>(L);
    private Func<string, string> GradeText => _gradeText ??= GridFilterText.ForEnum<AnalysisGrade>(L);

    private string CacheKey => IsServerScope ? $"releases:server:{ServerId}"
        : IsProjectScope ? $"releases:project:{ProjectId}"
        : "releases:global";

    // Server-paginated (global/project) scope cache key, distinct from the client-scope CacheKey above.
    // The sort is part of the key: two orders sharing one entry means the second is served the first
    // one's rows, which is indistinguishable from a sort that does nothing.
    private string PagedCacheKey(int page, int pageSize, int? projectId, string? sortBy = null, bool sortDescending = true) =>
        $"releases:paged:{projectId}:{page}:{pageSize}:{GridColumnFilters.CacheText(_columnFilters)}:{sortBy}:{sortDescending}";

    /// <summary>The project and pipeline names across the releases of this list's scope.</summary>
    private async Task LoadFilterValuesAsync()
    {
        try
        {
            _filterValues = IsServerScope
                ? await Api.Servers.GetServerReleaseFilterValuesAsync(ServerId!.Value)
                : await Api.Projects.GetReleaseFilterValuesAsync(ProjectId);
        }
        catch (HttpRequestException)
        {
            _filterValues = new ReleaseFilterValuesDto();
        }
    }

    private void ApplyReleasesPage(PaginatedResult<ReleaseDto> result)
    {
        _releases = result.Items;
        _totalCount = result.TotalCount;
    }

    protected override async Task OnInitializedAsync()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();

        // Pre-seed the paginated view so the first paint is instant. Both scopes are server-paged now,
        // so they share the same stale-while-revalidate shape; only the cache key differs.
        Cache.Seed<PaginatedResult<ReleaseDto>>(
            IsServerScope
                ? ServerPagedCacheKey(1, 20)
                : PagedCacheKey(1, 20, ProjectId ?? _projectFilter),
            ApplyReleasesPage);

        try
        {
            if (IsGlobal)
            {
                var projectsResult = await Api.Projects.GetProjectsAsync(pageSize: 100);
                _projects = projectsResult.Items;
            }
        }
        catch (HttpRequestException) { _projects = []; }
        await LoadFilterValuesAsync();

        // #8: on a project detail page the ProjectDetailLoader already runs a "releases" hub (for the
        // Overview tile). Reuse it in project scope - subscribe to its OnChanged and re-page the grid -
        // instead of opening a SECOND WebSocket to the same hub. Global/server scopes have no loader, so
        // they keep their own hub.
        if (IsProjectScope)
            ProjectLoader.OnChanged += OnProjectLoaderChanged;
        else
            await ConnectToHub();
    }

    // The project loader's change is live data, so the grid refreshes quietly.
    private void OnProjectLoaderChanged() => InvokeAsync(RefreshGrid);

    private string ServerPagedCacheKey(int page, int pageSize, string? sortBy = null, bool sortDescending = true) =>
        $"releases:server:{ServerId}:{page}:{pageSize}:{GridColumnFilters.CacheText(_columnFilters)}:{sortBy}:{sortDescending}";

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite()
    {
        _canWrite = Permissions.CanWrite(ResourceType.Release);
        _canAdminProject = IsProjectScope && Permissions.CanAdmin(ResourceType.Project, ProjectId);
    }

    // Page every scope on the server so opening a release list does not transfer the entire release
    // history.
    /// <summary>One page of the export, in the scope of the list, with the grid's sort and column
    /// filters.</summary>
    private Task<PaginatedResult<ReleaseDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(ReleaseDto.PublishedAt), fallbackDescending: true);
        var filters = args.ToApiFilters();
        return IsServerScope
            ? Api.Servers.GetServerReleasesAsync(ServerId!.Value, page, pageSize, sortBy, sortDescending, filters)
            : Api.Projects.GetReleasesAsync(page: page, pageSize: pageSize, projectId: ProjectId ?? _projectFilter,
                sortBy: sortBy, sortDescending: sortDescending, filters: filters);
    }

    private async Task OnLoadData(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(ReleaseDto.PublishedAt), fallbackDescending: true);
        _columnFilters = args.ToApiFilters();
        var filters = _columnFilters;
        if (IsServerScope)
        {
            await Cache.RevalidateAsync(
                ServerPagedCacheKey(page, pageSize, sortBy, sortDescending),
                () => Api.Servers.GetServerReleasesAsync(ServerId!.Value, page, pageSize, sortBy, sortDescending, filters),
                ApplyReleasesPage,
                loading => _loading = loading,
                () => InvokeAsync(StateHasChanged));
            return;
        }

        var projectId = ProjectId ?? _projectFilter;
        await Cache.RevalidateAsync(
            PagedCacheKey(page, pageSize, projectId, sortBy, sortDescending),
            () => Api.Projects.GetReleasesAsync(page: page, pageSize: pageSize, projectId: projectId,
                sortBy: sortBy, sortDescending: sortDescending, filters: filters),
            ApplyReleasesPage,
            loading => _loading = loading,
            () => InvokeAsync(StateHasChanged));
    }

    // Manual refresh: re-fetch server scope from source, then reload the grid for paginated scopes.
    private Task RefreshAsync() => ReloadGrid();

    private async Task OnProjectFilterChanged()
    {
        await ReloadGrid();
    }

    private async Task ClearFilters()
    {
        _projectFilter = null;
        await ReloadGrid();
    }

    private async Task SyncReleases()
    {
        // Global syncs the selected project; project scope syncs its own project.
        var targetProject = IsProjectScope ? ProjectId : _projectFilter;
        if (!targetProject.HasValue) return;

        _syncing = true;
        var synced = await Api.Projects.SyncReleasesAsync(targetProject.Value);
        if (synced.Count == 0)
            Toast.Info("Synced", "NoNewReleasesFound");
        else
            Toast.Success("Synced", "ReleasesSynced", synced.Count);
        await ReloadGrid();
        _syncing = false;
    }

    /// <summary>An administrator fixes the release number the next release of a version series takes:
    /// 1.4 and 0 make it 1.4.0. The dialog sends the number and stays open when the API refuses one a run of the series already took.</summary>
    private async Task SetNextVersion()
    {
        if (ProjectId is not { } projectId) return;
        var known = await Api.Projects.GetReleaseSeriesAsync(projectId) ?? [];
        // The dialog saves the number and announces it itself.
        await Dialog.OpenAsync<NextReleaseVersionDialog>(
            L["SetNextVersion"].Value,
            new Dictionary<string, object?> { ["KnownSeries"] = known, ["ProjectId"] = projectId },
            new OmniDialogOptions { Width = "480px", AutoFocusFirstElement = false });
    }

    private async Task TriggerBuild(ReleaseDto release)
    {
        var pipelines = await Api.Pipelines.GetPipelinesAsync(pageSize: 100);
        if (pipelines.Items.Count == 0)
        {
            Toast.Warning("NoPipelinesFound", "NoPipelinesFound");
            return;
        }

        var selectedPipelineId = await Dialog.OpenAsync<PipelineSelectDialog>(
            L["SelectPipeline"].Value,
            new Dictionary<string, object?> { { "Pipelines", pipelines.Items } },
            new OmniDialogOptions { Width = "400px", AutoFocusFirstElement = false });

        if (selectedPipelineId is int pipelineId)
        {
            var result = await Api.Projects.TriggerReleaseBuildAsync(release.Id, new TriggerReleaseBuildRequest { PipelineId = pipelineId });
            if (result is not null)
            {
                Toast.Success("BuildTriggered", release.Version);
                await ReloadGrid();
            }
        }
    }

    private Task RelaunchFromCommit(ReleaseDto release) =>
        new Releases.ArchivedReleaseRelauncher(Api, Dialog, Toast, Nav, L).RelaunchAsync(release);

    private async Task RollbackRelease(ReleaseDto release)
    {
        var preview = await Api.Projects.GetRollbackPreviewAsync(release.Id);
        if (preview is null || !preview.CanRollback || string.IsNullOrWhiteSpace(preview.TargetVersion))
        {
            Toast.Warning("Rollback", preview?.Reason ?? "RollbackUnavailable");
            return;
        }

        var pipelines = await Api.Pipelines.GetPipelinesAsync(pageSize: 100, projectId: release.ProjectId);
        var request = await Dialog.OpenAsync<RollbackReleaseDialog>(
            L["Rollback"].Value,
            new Dictionary<string, object?>
            {
                ["SourceVersion"] = release.Version,
                ["TargetVersion"] = preview.TargetVersion,
                ["DeploymentAge"] = RelativeTime.FormatAgo(L, release.PublishedAt ?? release.DetectedAt),
                ["ProjectId"] = release.ProjectId,
                ["Pipelines"] = pipelines.Items
            },
            new OmniDialogOptions { Width = "520px", AutoFocusFirstElement = false });
        if (request is not RollbackReleaseRequest rollbackRequest) return;

        var result = await Api.Projects.RollbackReleaseAsync(release.Id, rollbackRequest);
        if (result is not null)
        {
            Toast.Success("RollbackQueued", release.Version);
            await ReloadGrid();
        }
    }

    private async Task PromoteRelease(ReleaseDto release)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["PromoteConfirm"].Value, release.Version),
            L["Promote"].Value,
            new OmniConfirmOptions { OkButtonText = L["Promote"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var result = await Api.Projects.PromoteReleaseAsync(release.Id);
        if (result is not null)
        {
            Toast.Success("Promoted", release.Version);
            await ReloadGrid();
        }
    }

    /// <summary>Redeploy the previous production release through the pipeline that deployed the
    /// live one so deployment gates and approvals remain authoritative.</summary>
    private async Task RedeployRelease(ReleaseDto release)
    {
        if (release.RedeployPipelineId is not { } pipelineId) return;
        var confirmed = await Dialog.Confirm(
            string.Format(L["RedeployConfirm"].Value, release.Version),
            L["Redeploy"].Value,
            new OmniConfirmOptions { OkButtonText = L["Redeploy"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await TriggerRunAsync(pipelineId, new Dictionary<string, string> { ["candidateVersion"] = release.Version });
    }

    /// <summary>Switch traffic through the project's revert pipeline to the retained reserve colour
    /// without rebuilding; the agent refuses when that colour is unavailable.</summary>
    private async Task RollbackToPreviousColour(ReleaseDto release)
    {
        if (release.RevertPipelineId is not { } pipelineId) return;
        var confirmed = await Dialog.Confirm(
            string.Format(L["RevertToPreviousConfirm"].Value, release.Version),
            L["RevertToPrevious"].Value,
            new OmniConfirmOptions { Destructive = true, ConfirmIcon = OmniIconName.ArrowUUpLeft, OkButtonText = L["Rollback"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await TriggerRunAsync(pipelineId, []);
    }

    // The release actions confirmed the run and fixed its parameters: no preflight, no second prompt.
    private Task TriggerRunAsync(int pipelineId, Dictionary<string, string> parameters) =>
        new PipelineRunLauncher(Api, RunGate, RunDialogs, Nav, Toast, L).TriggerAsync(pipelineId, sourceBranch: null, parameters);

    private string PipelineHref(ReleaseDto release)
    {
        var pipelineId = release.SourcePipelineId!.Value;
        if (IsProjectScope)
            return $"/pipelines/{pipelineId}?projectId={ProjectId}";
        if (IsServerScope)
            return $"/pipelines/{pipelineId}?serverId={ServerId}";
        return $"/pipelines/{pipelineId}";
    }

    // --- Live refresh ---
    private bool _hubReloading;

    private async Task ConnectToHub()
    {
        _hubConnection = HubFactory.Create("releases");
        // R3W9: a batch update can carry deletes/reorders across a whole project; a blind row-patch would
        // silently mask a removed release (the finding's own caveat), so this path stays an authoritative
        // reload. A single create is a pure ADD - patched in place below.
        _hubConnection.On<int, List<ReleaseDto>>("ReleasesUpdated", (_, _) => HubReloadAsync());
        _hubConnection.On<ReleaseDto>("ReleaseCreated", OnReleaseCreated);
        _hubConnection.On<ReleaseDto>("ReleaseStatusChanged", OnReleaseStatusChanged);
        // Group membership is per-connection and lost on auto-reconnect - re-join + reload.
        _hubConnection.RejoinOnReconnect(async () =>
        {
            await _hubConnection.InvokeAsync("JoinAllReleases");
            await HubReloadAsync();
        });

        try
        {
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinAllReleases");
        }
        catch { /* Hub unavailable - degrade to static */ }
    }

    // R3W9: a status change only mutates an existing release - patch that one row via the record's
    // `with`-style replace and re-render just the grid (mirrors the Servers heartbeat patch), instead
    // of a full LoadData round-trip. Falls back to a reload when the release isn't on the visible page
    // (or in the server-scope in-memory list), where a targeted patch can't stay consistent.
    private Task OnReleaseStatusChanged(ReleaseDto release)
    {
        // Under a column filter the new status may take the row out of (or into) the filtered set,
        // so only the server can say what the page holds now.
        var pageIdx = _releases.FindIndex(r => r.Id == release.Id);
        if (pageIdx < 0 || _columnFilters.Count > 0)
            return HubReloadAsync();

        return InvokeAsync(() =>
        {
            _releases[pageIdx] = release;
            StateHasChanged();
        });
    }

    // R3W9: a freshly created release is a single ADD. Releases render newest-first, so the new row belongs
    // at the top - prepend it to the visible page and the server-scope in-memory list (upsert if a status
    // event already inserted it), instead of a full LoadData round-trip.
    private Task OnReleaseCreated(ReleaseDto release) =>
        InvokeAsync(() =>
        {
            if ((IsProjectScope && release.ProjectId != ProjectId)
                || (IsGlobal && _projectFilter is > 0 && release.ProjectId != _projectFilter))
                return;
            // A new release may not match the column filters; the server decides.
            if (IsServerScope || _columnFilters.Count > 0)
            {
                _ = HubReloadAsync();
                return;
            }
            var j = _releases.FindIndex(r => r.Id == release.Id);
            if (j >= 0) _releases[j] = release; else _releases.Insert(0, release);
            StateHasChanged();
        });

    private Task HubReloadAsync()
    {
        if (_hubReloading) return Task.CompletedTask;
        _hubReloading = true;
        return InvokeAsync(async () =>
        {
            try
            {
                // Live data refreshes quietly (rows, page, scroll and filters kept).
                await RefreshGrid();
            }
            catch (HttpRequestException) { } // hub-triggered refresh - silent on auth failure
            finally { _hubReloading = false; }
            StateHasChanged();
        });
    }

    private AetheusDataGrid<ReleaseDto>? _grid;
    private Task ReloadGrid() => _grid?.Reload() ?? Task.CompletedTask;
    private Task RefreshGrid() => _grid?.Refresh() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        if (IsProjectScope)
            ProjectLoader.OnChanged -= OnProjectLoaderChanged;
        if (_hubConnection is not null)
            await _hubConnection.DisposeAsync();
    }

    /// <summary>The export writes the name of every artifact of the release.</summary>
    private static readonly Func<ReleaseDto, object?> ArtifactExport = release => string.Join(", ", release.Artifacts.Select(artifact => artifact.Name));
}
