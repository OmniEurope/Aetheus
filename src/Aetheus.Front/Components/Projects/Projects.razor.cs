// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Components.Projects;

public partial class Projects : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    private List<ProjectDto> _projects = [];
    private int _totalCount;
    private bool _loading;
    private bool _canWrite;
    private string? _search;
    private ProjectStatus? _statusFilter;
    private ProjectSortOption _sortOption = ProjectSortOption.Activity;
    private ProjectViewMode _viewMode = ProjectViewMode.Comfortable;
    private bool _favoritesOnly;
    private HashSet<int> _favoriteProjectIds = [];

    private List<OmniOption<ProjectStatus?>> _statusOptions = [];
    private List<OmniOption<ProjectSortOption>> _sortOptions = [];
    private HubConnection? _hubConnection;
    private HubConnection? _pipelineHub;
    private readonly TrailingReloadCoalescer _pipelineReload = new(1000);
    private const string FavoriteStorageKey = "aetheus.projects.favorites";
    private const string ViewModeStorageKey = "aetheus.projects.view-mode";

    private string CacheKey => $"projects:{_search}:{_statusFilter}";

    private void ApplyProjects(PaginatedResult<ProjectDto> result)
    {
        _projects = result.Items;
        _totalCount = result.TotalCount;
    }

    protected override async Task OnInitializedAsync()
    {
        _statusOptions =
        [
            new((ProjectStatus?)ProjectStatus.Active, L["Active"].Value),
            new((ProjectStatus?)ProjectStatus.Archived, L["Archived"].Value)
        ];
        _sortOptions =
        [
            new(ProjectSortOption.Activity, L["SortByActivity"].Value),
            new(ProjectSortOption.NameAscending, L["SortNameAscending"].Value),
            new(ProjectSortOption.NameDescending, L["SortNameDescending"].Value)
        ];
        Breadcrumb.Set(new BreadcrumbItem(L["Projects"]));
        // MainLayout loads permissions in parallel with route activation, so this
        // page can mount BEFORE Permissions.IsLoaded - capturing _canWrite=false
        // would then strand the "New Project" button as permanently disabled.
        // Subscribe to the change event so the button reactivates the moment
        // permissions land. (The race only bites cold-boot navigations like the
        // ones the E2E suite drives, but the bug is real for users too.)
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        Cache.Seed<PaginatedResult<ProjectDto>>(CacheKey, ApplyProjects);
        try
        {
            await LoadData();
        }
        catch (HttpRequestException) { } // 401 on expired JWT - redirect handled by AuthProvider
        await StartHubAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        try
        {
            var favoritesJson = await JS.InvokeAsync<string?>("localStorage.getItem", FavoriteStorageKey);
            if (!string.IsNullOrWhiteSpace(favoritesJson))
                _favoriteProjectIds = JsonSerializer.Deserialize<HashSet<int>>(favoritesJson) ?? [];

            var viewMode = await JS.InvokeAsync<string?>("localStorage.getItem", ViewModeStorageKey);
            if (Enum.TryParse<ProjectViewMode>(viewMode, ignoreCase: true, out var parsed))
                _viewMode = parsed;
        }
        catch (JSException)
        {
            // Preferences are an enhancement; the project list remains fully usable without storage.
        }

        StateHasChanged();
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() =>
        _canWrite = Permissions.CanWrite(Aetheus.Shared.Components.Auth.ResourceType.Project);

    private async Task StartHubAsync()
    {
        try
        {
            _hubConnection = HubFactory.Create("entities");
            _hubConnection.On<ResourceType, int, string>("EntityChanged", (type, _, _) =>
            {
                if (type == ResourceType.Project)
                    return InvokeAsync(LoadDataAndRender);
                return Task.CompletedTask;
            });
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
                await LoadDataAndRender();
            }));
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
        }
        catch
        {
            // best-effort: SignalR is non-critical for the page.
        }

        try
        {
            _pipelineHub = HubFactory.Create("pipelines");
            _pipelineHub.On<int, int>("PipelineRunStarted", (_, _) => RequestPipelineReloadAsync());
            _pipelineHub.On<int, PipelineStatus>("PipelineRunCompleted", (_, _) => RequestPipelineReloadAsync());
            _pipelineHub.On<int>("PipelineRunCancelled", _ => RequestPipelineReloadAsync());
            _pipelineHub.On<int, string, string>("ApprovalRequired", (_, _, _) => RequestPipelineReloadAsync());
            _pipelineHub.On<int, string, string>("ApprovalResolved", (_, _, _) => RequestPipelineReloadAsync());
            _pipelineHub.RejoinOnReconnect(async () =>
            {
                await _pipelineHub.InvokeAsync("JoinPipelineUpdatesGroup");
                await LoadDataAndRender();
            });
            await _pipelineHub.StartAsync();
            await _pipelineHub.InvokeAsync("JoinPipelineUpdatesGroup");
        }
        catch { /* best-effort: the project list remains usable without live updates */ }
    }

    private Task RequestPipelineReloadAsync() =>
        InvokeAsync(() => _pipelineReload.RequestAsync(() => InvokeAsync(LoadDataAndRender)));

    private async Task LoadDataAndRender()
    {
        await LoadData();
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        if (_hubConnection is not null)
        {
            await _hubConnection.LeaveEntityUpdatesAndDisposeAsync(ResourceType.Project);
            _hubConnection = null;
        }
        if (_pipelineHub is not null)
        {
            try { await _pipelineHub.InvokeAsync("LeavePipelineUpdatesGroup"); } catch { /* best-effort */ }
            await _pipelineHub.DisposeAsync();
            _pipelineHub = null;
        }
    }

    private async Task LoadData()
    {
        await Cache.RevalidateAsync(
            CacheKey,
            FetchAllProjectsAsync,
            ApplyProjects,
            loading => _loading = loading,
            () => InvokeAsync(StateHasChanged));
    }

    private async Task<PaginatedResult<ProjectDto>> FetchAllProjectsAsync()
    {
        var items = await Api.Projects.GetAllProjectsAsync(
            search: _search,
            status: _statusFilter,
            sortBy: "name");
        return new PaginatedResult<ProjectDto>
        {
            Items = items,
            TotalCount = items.Count,
            Page = 1,
            PageSize = items.Count
        };
    }

    private IReadOnlyList<ProjectDto> VisibleProjects
    {
        get
        {
            IEnumerable<ProjectDto> query = _projects;
            if (_favoritesOnly)
                query = query.Where(project => _favoriteProjectIds.Contains(project.Id));

            query = _sortOption switch
            {
                ProjectSortOption.NameAscending => query
                    .OrderByDescending(project => _favoriteProjectIds.Contains(project.Id))
                    .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase),
                ProjectSortOption.NameDescending => query
                    .OrderByDescending(project => _favoriteProjectIds.Contains(project.Id))
                    .ThenByDescending(project => project.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => query
                    .OrderByDescending(project => _favoriteProjectIds.Contains(project.Id))
                    .ThenByDescending(ActivityAt)
                    .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            };
            return query.ToList();
        }
    }

    // Recette R-319: the last commit is the project's, from any repository its runs cloned; the commit
    // page finds the repository that holds it (or explains that none of the internal ones does).
    private static string GitCommitHref(ProjectDto project) => $"/git-repositories/commits/{project.LastCommitId}";

    private static DateTime ActivityAt(ProjectDto project)
    {
        var activity = project.UpdatedAt;
        if (project.LastCommitAt is { } commitAt && commitAt > activity) activity = commitAt;
        if (project.LastRunAt is { } runAt && runAt > activity) activity = runAt;
        return activity;
    }

    private async Task ApplyFilters()
    {
        await LoadData();
    }

    private async Task ClearFilters()
    {
        _search = null;
        _statusFilter = null;
        _favoritesOnly = false;
        await LoadData();
    }

    private void ToggleFavoritesOnly() => _favoritesOnly = !_favoritesOnly;

    private async Task ToggleFavoriteAsync(ProjectDto project)
    {
        if (!_favoriteProjectIds.Add(project.Id))
            _favoriteProjectIds.Remove(project.Id);

        try
        {
            await JS.InvokeVoidAsync(
                "localStorage.setItem",
                FavoriteStorageKey,
                JsonSerializer.Serialize(_favoriteProjectIds));
        }
        catch (JSException)
        {
            // Keep the in-memory preference for this session when storage is unavailable.
        }
    }

    private async Task SetViewModeAsync(ProjectViewMode mode)
    {
        _viewMode = mode;
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", ViewModeStorageKey, mode.ToString());
        }
        catch (JSException)
        {
            // Keep the selected mode for this session when storage is unavailable.
        }
    }

    /// <summary>Opens the create-project dialog; reloads the list when a project is created.</summary>
    private async Task OpenCreateDialog()
    {
        var result = await Dialog.OpenAsync<ProjectFormDialog>(
            L["NewProject"],
            new Dictionary<string, object?>(),
            ProjectFormDialog.OmniDialogOptions());
        if (result is ProjectDto created)
        {
            await LoadDataAndRender();
            // Recette R2-121: a project is only its record until its pipelines exist. Offer the next
            // step, the setup wizard for this project, whose readiness check names each missing
            // prerequisite (repository, runner, environments, libraries, adapters) with its fix.
            var setUp = await Dialog.Confirm(
                string.Format(L["SetUpNewProjectPipelinesConfirm"].Value, created.Name),
                L["ProjectCreated"].Value,
                new OmniConfirmOptions { OkButtonText = L["SetUpPipelines"].Value, CancelButtonText = L["StayHere"].Value });
            if (setUp == true)
                Nav.NavigateTo($"/pipelines/setup?projectId={created.Id}");
        }
    }

    private void OpenProject(int projectId) => Nav.NavigateTo($"/projects/{projectId}");

    private void OnProjectKeyDown(KeyboardEventArgs args, int projectId)
    {
        if (args.Key is "Enter" or " ")
            OpenProject(projectId);
    }

    private static string LastCommitSha(ProjectDto project) =>
        string.IsNullOrWhiteSpace(project.LastCommitSha) ? "-" : project.LastCommitSha;

    private static string ProductionStatusClass(ProjectProductionStatus status) =>
        status switch
        {
            ProjectProductionStatus.Online => "online",
            ProjectProductionStatus.Offline => "offline",
            _ => "unavailable"
        };

    /// <summary>The first line of a commit message: the tile shows one line, the title carries the rest.</summary>
    internal static string FirstLine(string message) =>
        message.Split('\n', 2)[0].TrimEnd('\r').Trim();

    private static string TruncateText(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return string.Concat(text.AsSpan(0, maxLength - 3), "...");
    }

    private enum ProjectSortOption
    {
        Activity,
        NameAscending,
        NameDescending
    }

    private enum ProjectViewMode
    {
        Comfortable,
        Dense
    }
}
