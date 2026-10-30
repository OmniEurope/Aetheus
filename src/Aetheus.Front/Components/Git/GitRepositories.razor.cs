// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Git;

public partial class GitRepositories : IAsyncDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ILogger<GitRepositories> Logger { get; set; } = default!;

    private HubConnection? _hub;
    private CancellationTokenSource? _loadCts;
    // Cancelled when the page is left, so a realtime connection still starting stops with it.
    private readonly CancellationTokenSource _lifetime = new();
    private int _loadGeneration;
    private AetheusDataGrid<GitLightRepoDto>? _grid;
    private readonly HashSet<int> _joinedRepositoryIds = [];

    [SupplyParameterFromQuery(Name = "projectId")]
    public int? ProjectId { get; set; }

    [SupplyParameterFromQuery(Name = "create")]
    public bool CreateRequested { get; set; }

    private List<GitLightRepoDto> _repos = [];
    private int _totalCount;
    private List<ProjectDto> _projects = [];
    private bool _projectsLoaded;
    private string? _search;
    private bool _loading;
    private bool _canWrite;
    private int? _projectFilter;
    private int? _lastAppliedProjectId;
    private int _currentPage = 1;
    private int _pageSize = 20;
    private string _sortBy = "Name";
    private bool _sortDescending;
    private bool _createDialogOpened;

    // The grid's header filters, sent with every page request, and the values they offer.
    private List<GridFilter> _columnFilters = [];
    private GitRepositoryFilterValuesDto _filterValues = new();
    private (bool Loaded, int? ProjectId) _filterValuesScope;
    private IReadOnlyList<string> _projectNames = [];
    private Func<string, string>? _emptyText;
    private Func<string, string> EmptyText => _emptyText ??= value =>
        bool.TryParse(value, out var empty) ? L[empty ? "Empty" : "Active"].Value : value;
    internal bool IsLoading => _loading;
    internal int? ActiveProjectFilter => _projectFilter;
    internal IReadOnlyList<GitLightRepoDto> Repositories => _repos;
    internal int RepositoryCount => _totalCount;
    private string GitGridClass => !_loading && _totalCount == 0
        ? "pipeline-list-grid git-repositories-grid-empty"
        : "pipeline-list-grid";

    protected override async Task OnInitializedAsync()
    {
        Breadcrumb.Set(new BreadcrumbItem(L["GitRepositories"]));
        // Subscribe BEFORE reading: MainLayout loads permissions in parallel with
        // route activation, so the page can mount before they're ready. Without
        // the subscription, _canWrite would stay false until the next navigation
        // - stranding "New Repository" as permanently disabled.
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        // The grid starts its own first load at the first render, which happens at the first await
        // below. The project scope is set before it, so that load is the right one and the only
        // one; OnParametersSetAsync then finds the project already applied.
        _lastAppliedProjectId = ProjectId;
        ApplyProjectFilterFromQuery();
        var requestedScope = _projectFilter;
        try
        {
            _projects = await Api.Projects.GetAllProjectsAsync();
            _projectsLoaded = true;
            _projectNames = [.. _projects.Select(project => project.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            GitLoadFailureLogger.Log(Logger, ex, "projects", detailPage: false);
            _projects = [];
            _projectsLoaded = false;
        }
        ApplyProjectFilterFromQuery();
        SetTrail();
        await LoadFilterValuesAsync();
        // Only an unknown project (dropped once the projects are known) changes the scope the grid
        // already loaded; that case alone loads again.
        if (_projectFilter != requestedScope)
            await ResetAndReloadAsync();
        await ConnectHub();
    }

    private async Task ConnectHub()
    {
        // Left while the page was still loading: DisposeAsync has run, and a hub created now would
        // never be disposed. IsCancellationRequested stays readable after the source is disposed.
        if (_lifetime.IsCancellationRequested) return;
        var lifetime = _lifetime.Token;
        try
        {
            _hub = HubFactory.Create("git");
            // Live events refresh the rows in place (page, sort and filters kept).
            _hub.On<int>("RepositoryChanged", _ => InvokeAsync(LiveRefreshAsync));
            _hub.On<int>("CommitsChanged", _ => InvokeAsync(LiveRefreshAsync));
            _hub.On<int>("BranchesChanged", _ => InvokeAsync(LiveRefreshAsync));
            // Group membership is per-connection and lost on auto-reconnect - re-join + reload.
            _hub.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                _joinedRepositoryIds.Clear();
                await LiveRefreshAsync();
            }));
            await _hub.StartAsync(lifetime);
            await JoinVisibleRepositoryGroupsAsync();
        }
        // Hub unavailable (server down/restarting, negotiation rejected, or navigation
        // cancelled the connect) - the page works without real-time updates. Silent by
        // design: pages have no ILogger by convention.
        // A server that negotiates but whose transports all fail is reported as an AggregateException.
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
            or AggregateException)
        {
        }
        // The page was left while the connection was starting and DisposeAsync stopped it half-way:
        // whatever that start reports belongs to no page any more, and must not reach the error
        // boundary, which would show it on the page the user went to.
        catch (Exception ex) when (lifetime.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Git repositories live connection stopped because the page was left");
        }
    }

    private async Task JoinVisibleRepositoryGroupsAsync()
    {
        if (_hub?.State != HubConnectionState.Connected) return;
        var visible = _repos.Select(repository => repository.Id).ToHashSet();
        foreach (var repositoryId in _joinedRepositoryIds.Except(visible).ToList())
        {
            await _hub.InvokeAsync("LeaveRepositoryGroup", repositoryId);
            _joinedRepositoryIds.Remove(repositoryId);
        }
        foreach (var repositoryId in visible.Except(_joinedRepositoryIds))
        {
            await _hub.InvokeAsync("JoinRepositoryGroup", repositoryId);
            _joinedRepositoryIds.Add(repositoryId);
        }
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_createDialogOpened || !CreateRequested || !_projectFilter.HasValue || !_canWrite) return;
        _createDialogOpened = true;
        await ShowCreateDialog();
        Nav.NavigateTo($"git-repositories?projectId={_projectFilter.Value}", replace: true);
    }

    private void RefreshCanWrite() =>
        _canWrite = Permissions.CanWrite(Aetheus.Shared.Components.Auth.ResourceType.Project);

    protected override async Task OnParametersSetAsync()
    {
        if (_lastAppliedProjectId == ProjectId)
        {
            return;
        }

        _lastAppliedProjectId = ProjectId;
        ApplyProjectFilterFromQuery();
        await LoadFilterValuesAsync();
        await ResetAndReloadAsync();
    }

    internal async Task LoadData()
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        using var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCts, cts);
        previous?.Cancel();
        _loading = true;
        // No project filter => list every repo the user can access (backend cross-project view);
        // a filter narrows to that project. try/catch for the OnInitializedAsync path: an expired-JWT
        // 401 must fall through to an empty list, not trip the ErrorBoundary during the auth redirect.
        try
        {
            var result = await Api.Git.GetGitReposPageAsync(
                _currentPage, _pageSize, _projectFilter, _search, _sortBy, _sortDescending, cts.Token, _columnFilters);
            if (generation != _loadGeneration) return;
            _repos = result.Items;
            _totalCount = result.TotalCount;
            await JoinVisibleRepositoryGroupsAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            if (generation != _loadGeneration) return;
            GitLoadFailureLogger.Log(Logger, ex, "repositories", detailPage: false);
            _repos = [];
            _totalCount = 0;
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _loading = false;
                Interlocked.CompareExchange(ref _loadCts, null, cts);
            }
        }
    }

    /// <summary>Page-side refreshes go through the virtualized grid's loader so competing requests
    /// cannot replace its current block with stale rows.</summary>
    private async Task RefreshGridAsync()
    {
        if (_grid is not null)
            await _grid.Reload();
        else
            await LoadData();
        StateHasChanged();
    }

    /// <summary>A live change fetches the grid's current page again without the loader.</summary>
    private async Task LiveRefreshAsync()
    {
        if (_grid is not null)
            await _grid.Refresh();
        else
            await LoadData();
        StateHasChanged();
    }

    /// <summary>The default branches offered by the column filter, in the list's scope.</summary>
    private async Task LoadFilterValuesAsync()
    {
        if (_filterValuesScope == (true, _projectFilter)) return;
        _filterValuesScope = (true, _projectFilter);
        try
        {
            _filterValues = await Api.Git.GetGitRepoFilterValuesAsync(_projectFilter);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _filterValues = new GitRepositoryFilterValuesDto();
        }
    }

    /// <summary>One page of the export, with the project filter, search, sort and column filters of
    /// the grid.</summary>
    internal Task<PaginatedResult<GitLightRepoDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest("Name");
        return Api.Git.GetGitReposPageAsync(page, pageSize, _projectFilter, _search, sortBy, sortDescending,
            CancellationToken.None, args.ToApiFilters());
    }

    internal async Task OnLoadDataAsync(GridLoadArgs args)
    {
        (_currentPage, _pageSize) = args.ToPageRequest();
        (_sortBy, _sortDescending) = args.ToSortRequest("Name");
        _columnFilters = args.ToApiFilters();
        await LoadData();
        // A project holding a single repository opens it, once, after the first load the grid made.
        if (!_firstLoadDone)
        {
            _firstLoadDone = true;
            RedirectToOnlyRepositoryIfNeeded();
        }
    }

    private bool _firstLoadDone;

    // The project's internal repositories, then its external repository.
    private static readonly IReadOnlyList<string> RepositoryTabSlugs = ["internal", "external"];
    private int _repositoryTab;

    private bool ShowsExternalTab => _projectFilter.HasValue && _repositoryTab == 1;

    /// <summary>The public repository a project is built from, when it has one and no internal
    /// repository; the empty state then links to it rather than reading "no repository".</summary>
    private string? ExternalRepositoryUrl => _projectFilter is { } projectId
        && _projects.FirstOrDefault(project => project.Id == projectId)?.RepositoryUrl is { Length: > 0 } url
        ? url
        : null;

    private async Task ResetAndReloadAsync()
    {
        if (_grid is not null)
        {
            // Forced: a remote-virtualized grid reports page 1 whatever its scroll position.
            _currentPage = 1;
            await _grid.GoToPage(0, forceReload: true);
            return;
        }

        _currentPage = 1;
        await RefreshGridAsync();
    }

    private async Task ShowCreateDialog()
    {
        if (!_projectFilter.HasValue) return;

        var name = await Dialog.OpenAsync<GitRepositoryCreateDialog>(
            L["NewRepository"].Value,
            new Dictionary<string, object?> { { "ProjectId", _projectFilter.Value } },
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });

        if (name is true)
        {
            await ResetAndReloadAsync();
        }
    }

    private async Task DeleteRepo(GitLightRepoDto repo)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["DeleteConfirm"].Value, repo.Name),
            L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Veil.RunAsync(async () =>
        {
            var result = await Api.Git.DeleteGitRepoAsync(repo.Id);
            if (result)
            {
                Toast.Success("Deleted", repo.Name);
                await ResetAndReloadAsync();
            }
            else
            {
                Toast.Error("Error", "DeleteFailed");
            }
        });
    }

    private async Task ClearFilters()
    {
        _projectFilter = null;
        SyncUrlWithCurrentFilter();
        await LoadFilterValuesAsync();
        await ResetAndReloadAsync();
    }

    private async Task OnProjectFilterChanged(object _)
    {
        SyncUrlWithCurrentFilter();
        await LoadFilterValuesAsync();
        await ResetAndReloadAsync();
    }

    /// <summary>Inside a project the trail names it (Projects / project / Git), which also replaces
    /// the generic page description on the second header line.</summary>
    private void SetTrail()
    {
        if (ProjectId is not { } projectId || _projects.FirstOrDefault(project => project.Id == projectId) is not { } project)
            return;
        Breadcrumb.Set(
            new BreadcrumbItem(L["Projects"], "/projects"),
            new BreadcrumbItem(project.Name, $"/projects/{projectId}/overview"),
            new BreadcrumbItem(L["GitRepositories"]));
    }

    private void ApplyProjectFilterFromQuery()
    {
        if (!ProjectId.HasValue)
        {
            _projectFilter = null;
            return;
        }

        _projectFilter = !_projectsLoaded || _projects.Any(p => p.Id == ProjectId.Value)
            ? ProjectId.Value
            : null;
    }

    private void SyncUrlWithCurrentFilter()
    {
        var target = _projectFilter.HasValue
            ? $"git-repositories?projectId={_projectFilter.Value}"
            : "git-repositories";

        var current = Nav.ToBaseRelativePath(Nav.Uri);
        if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
        {
            Nav.NavigateTo(target, replace: true);
        }
    }

    private bool RedirectToOnlyRepositoryIfNeeded()
    {
        if (CreateRequested || !_projectFilter.HasValue || !string.IsNullOrWhiteSpace(_search)
            || ShowsExternalTab || _currentPage != 1 || _totalCount != 1 || _repos.Count != 1)
            return false;

        var repository = _repos[0];
        Nav.NavigateTo($"git-repositories/{repository.Id}?projectId={repository.ProjectId}", replace: true);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        // Disposed once: a second call would Cancel() the already disposed lifetime.
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        Interlocked.Increment(ref _loadGeneration);
        var cts = Interlocked.Exchange(ref _loadCts, null);
        cts?.Cancel();
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        if (_hub is not null)
        {
            // SignalR removes every repository-group membership when the connection closes.
            // Do not invoke the removed global-group contract during teardown: disposal errors
            // escape the component lifecycle and trigger Blazor's global error boundary.
            try { await _hub.DisposeAsync(); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException) { }
        }
        _joinedRepositoryIds.Clear();
        _lifetime.Dispose();
    }

    /// <summary>The export writes the state the badge shows.</summary>
    private Func<GitLightRepoDto, object?> StatusExport => _statusExport ??= repository => L[repository.IsEmpty ? "Empty" : "Active"].Value;
    private Func<GitLightRepoDto, object?>? _statusExport;
}
