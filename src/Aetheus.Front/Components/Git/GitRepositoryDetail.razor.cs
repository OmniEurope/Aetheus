// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Markdig;
namespace Aetheus.Front.Components.Git;

public partial class GitRepositoryDetail : IAsyncDisposable
{
    [Parameter] public int Id { get; set; }
    [SupplyParameterFromQuery(Name = "search")] public string? CommitSearch { get; set; }
    [SupplyParameterFromQuery(Name = "branch")] public string? BranchSearch { get; set; }
    [SupplyParameterFromQuery(Name = "ref")] public string? FileRef { get; set; }
    [SupplyParameterFromQuery(Name = "path")] public string? FilePath { get; set; }
    [SupplyParameterFromQuery(Name = "line")] public int? FileLine { get; set; }
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;
    [Inject] private ILogger<GitRepositoryDetail> Logger { get; set; } = default!;
    private string CommitsCacheKey(int page, int pageSize) =>
        GitRepositoryCacheKeys.Commits(Id, page, pageSize);
    private string PrsCacheKey(int page, int pageSize, string? sortBy = null, bool sortDescending = true) =>
        GitRepositoryCacheKeys.PullRequests(Id, page, pageSize, sortBy, sortDescending);
    private GitRepositoryRealtimeSubscription? _realtime;
    private GitRepositoryExportReader? _exports;
    private GitRepositoryExportReader Exports => _exports ??= new(Api.Git);
    private bool _canWrite;
    private int? _loadedId;
    private string? _loadedCommitSearch, _loadedBranchSearch, _loadedFileRef, _loadedFilePath;
    private int? _loadedFileLine;
    private int _dataGeneration;
    private GitRepositoryDetailActions Actions => new(Api, Dialog, Toast, L, Veil);
    private GitLightRepoDto? _repo;
    private bool _loading = true;
    private int _selectedTab;
    // Ordered tab slugs for UrlSyncedTabs (?tab=). Mirrors the markup; Readme is conditional.
    private string[] TabSlugs => string.IsNullOrEmpty(_readmeHtml)
        ? ["commits", "branches", "tags", "files", "pulls", "graph", "protection"]
        : ["commits", "branches", "tags", "files", "pulls", "graph", "readme", "protection"];
    // Commits
    // Any failure: a git revision walk can fail in more ways than the transport, and the tab shows them all.
    private readonly GitPagedList<GitLightCommitDto> _commits = new(static _ => true);
    // The zip download in the header actions.
    private bool _downloadingArchive;
    // Branches
    // _branches: the first block, what the ref pickers offer; _branchRows: the block the grid last fetched.
    private List<GitLightBranchDto> _branches = [];
    private List<GitLightBranchDto> _branchRows = [];
    private readonly GitRepositoryPageState _branchPage = new();
    // Tags
    private List<GitLightTagDto> _tags = [];
    private readonly GitRepositoryPageState _tagPage = new();
    // Files
    private List<GitLightTreeEntryDto> _treeEntries = [];
    private bool _treeLoading;
    private bool _treeError;
    private int _treeTotalCount;
    private string? _treeRef;
    private string? _treePath;
    private GitLightBlobDto? _blobContent;
    private readonly string _monacoElementId = $"monaco-viewer-{Guid.NewGuid():N}";
    private bool _pendingMonacoInit;
    private bool _monacoInitialized;
    // Pull Requests
    private readonly GitPagedList<InternalPullRequestDto> _pullRequests =
        new(static ex => ex is HttpRequestException or JsonException);
    // README
    private string? _readmeContent;
    private string? _readmeHtml;
    // Branch Protection
    // _protectionRules: the first block, read by the branch lock icons; _protectionRows: the grid's last block.
    private List<BranchProtectionRuleDto> _protectionRules = [];
    private List<BranchProtectionRuleDto> _protectionRows = [];
    private readonly GitRepositoryPageState _protectionPage = new();
    // Commit Graph
    private List<GitLightCommitDto> _graphCommits = [];
    private bool _graphLoading;
    private bool _graphError;
    // The grids, their kept header filters and the values those offer.
    private readonly GitRepositoryGrids _grids = new();
    // Blame
    private List<GitLightBlameLine>? _blameLines;
    private string? _blameFormatted;
    protected override Task OnInitializedAsync()
    {
        // Subscribe BEFORE loading: MainLayout resolves permissions in parallel with route
        // activation, so this page can mount before they're ready. Without the subscription,
        // _canWrite would stay false until the next navigation, stranding the write actions as
        // permanently disabled. Mirrors the GitRepositories list pattern.
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        return Task.CompletedTask;
    }
    protected override async Task OnParametersSetAsync()
    {
        if (_loadedId == Id && string.Equals(_loadedCommitSearch, CommitSearch, StringComparison.Ordinal)
            && string.Equals(_loadedBranchSearch, BranchSearch, StringComparison.Ordinal)
            && string.Equals(_loadedFileRef, FileRef, StringComparison.Ordinal)
            && string.Equals(_loadedFilePath, FilePath, StringComparison.Ordinal)
            && _loadedFileLine == FileLine) return;
        var previousId = _loadedId;
        _loadedId = Id;
        _loadedCommitSearch = CommitSearch;
        _loadedBranchSearch = BranchSearch;
        _loadedFileRef = FileRef;
        _loadedFilePath = FilePath;
        _loadedFileLine = FileLine;
        // A ?search= link is the Message column's default filter (DefaultFilterValue); the first
        // load, made before the grid exists, sends the same filter.
        _grids.CommitFilters = GitRepositoryGrids.MessageFilter(CommitSearch);
        var generation = ++_dataGeneration;
        await ResetRepositoryStateAsync(previousId);
        await LoadData();
        // A load superseded by another repository (or by the page's disposal) must not connect: it would
        // replace the current subscription, which would then never be disposed.
        if (generation != _dataGeneration) return;
        if (_repo is not null) await ConnectHub();
    }
    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }
    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.Project);
    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        _dataGeneration++;
        await DisposeHubAsync(_loadedId);
        await DisposeMonacoAsync();
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_pendingMonacoInit && _blobContent is not null)
        {
            _pendingMonacoInit = false;
            var lang = GitRepositoryViewHelpers.InferLanguage(_blobContent.Path);
            await JS.InvokeVoidAsync("monacoInterop.initReadOnly", _monacoElementId, _blobContent.Content, lang, true, FileLine);
            _monacoInitialized = true;
        }
    }
    private async Task ResetRepositoryStateAsync(int? previousId)
    {
        await DisposeHubAsync(previousId);
        await DisposeMonacoAsync();
        _repo = null;
        _treeRef = null;
        _treePath = null;
        _blobContent = null;
        ClearBlame();
        _readmeContent = null;
        _readmeHtml = null;
        _commits.Clear();
        _branches = [];
        _branchRows = [];
        _tags = [];
        _treeEntries = [];
        _pullRequests.Clear();
        _protectionRules = [];
        _protectionRows = [];
        _graphCommits = [];
        _grids.Values = new();
    }
    private async Task DisposeHubAsync(int? repositoryId)
    {
        _ = repositoryId;
        var realtime = _realtime;
        _realtime = null;
        if (realtime is not null) await realtime.DisposeAsync();
    }
    private async Task DisposeMonacoAsync()
    {
        if (!_monacoInitialized) return;
        try { await JS.InvokeVoidAsync("monacoInterop.disposeEditor", _monacoElementId); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException) { } // teardown: the editor or the JS runtime may already be gone
        _monacoInitialized = false;
    }
    private async Task LoadData()
    {
        var generation = _dataGeneration;
        var repositoryId = Id;
        _loading = true;
        // The loader below replaces the tab strip, so every grid it held is disposed.
        _grids.ForgetMountedGrids();
        // Contains initial repository-load failures so the page can show its empty state and let
        // authentication redirects complete without invoking ErrorBoundary.
        GitLightRepoDto? repository;
        try { repository = await Api.Git.GetGitRepoAsync(repositoryId); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            GitLoadFailureLogger.Log(Logger, ex, "repository", detailPage: true);
            repository = null;
        }
        if (generation != _dataGeneration || repositoryId != Id) return;
        _repo = repository;
        _loading = false;
        if (_repo is null) return;
        Breadcrumb.Set(
            new BreadcrumbItem(L["Projects"], "/projects"),
            new BreadcrumbItem(_repo.ProjectName ?? $"{L["Project"]} #{_repo.ProjectId}", $"/projects/{_repo.ProjectId}/overview"),
            new BreadcrumbItem(L["GitRepositories"], $"/git-repositories?projectId={_repo.ProjectId}"),
            new BreadcrumbItem(_repo.Name));
        _treeRef ??= string.IsNullOrWhiteSpace(FileRef) ? _repo.DefaultBranch : FileRef;
        // Isolate each sub-resource: one slow/failed loader (e.g. a git timeout on
        // commits) must not collapse the whole page to the ErrorBoundary.
        await Task.WhenAll(
            SafeLoad(LoadBranches), SafeLoad(LoadTags), SafeLoad(LoadCommits), SafeLoad(LoadTree),
            SafeLoad(LoadPrs), SafeLoad(LoadProtectionRules), SafeLoad(LoadGraph),
            SafeLoad(() => _grids.LoadValuesAsync(Api.Git, repositoryId, () => Id)));
        if (!string.IsNullOrWhiteSpace(FilePath)) await SafeLoad(OpenDeepLinkedFileAsync);
        await SafeLoad(LoadReadme);
    }
    private async Task SafeLoad(Func<Task> load)
    {
        // Loaders surface their own inline error state; this just contains the
        // exception so a single failure doesn't crash the page.
        try { await load().ConfigureAwait(false); }
        catch (HttpRequestException ex) { GitLoadFailureLogger.Log(Logger, ex, "sub-resource", detailPage: true); }
        catch (JsonException ex) { GitLoadFailureLogger.Log(Logger, ex, "sub-resource", detailPage: true); }
        catch (TaskCanceledException) { }
    }
    private async Task LoadPageAsync<T>(
        GitRepositoryPageState state, Func<Task<PaginatedResult<T>>> fetch,
        Action<List<T>> apply)
    {
        var generation = _dataGeneration;
        state.Loading = true;
        state.Error = false;
        try
        {
            var result = await fetch();
            if (generation != _dataGeneration) return;
            apply(result.Items);
            state.TotalCount = result.TotalCount;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            if (generation != _dataGeneration) return;
            GitLoadFailureLogger.Log(Logger, ex, "paged sub-resource", detailPage: true);
            apply([]);
            state.TotalCount = 0;
            state.Error = true;
        }
        finally
        {
            if (generation == _dataGeneration) state.Loading = false;
        }
    }

    // ── Commits ──────────────────────────────────────────────────────
    // The grid is not rendered while the commits failed to load (the alert replaces it).
    private AetheusDataGrid<GitLightCommitDto>? MountedCommitsGrid => _commits.Error ? null : _grids.Commits;
    // The grid walks every ref; its Branch column filter narrows the walk to the ticked branches.
    private Task LoadCommits() =>
        _commits.LoadAsync(() => Api.Git.GetGitCommitsAsync(Id, GitReference.AllBranches, filters: _grids.CommitFilters));
    private Task OnLoadCommits(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(30);
        _grids.CommitFilters = args.ToApiFilters();
        // S-TECH-SWGT: render the last cached page instantly, then revalidate; on a cache hit a failure
        // leaves the cached rows on screen.
        var key = $"{CommitsCacheKey(page, pageSize)}:{GridColumnFilters.CacheText(_grids.CommitFilters)}";
        return _commits.LoadCachedAsync(Cache, key,
            () => Api.Git.GetGitCommitsAsync(Id, GitReference.AllBranches, page, pageSize, search: null, _grids.CommitFilters));
    }
    // ── Branches ─────────────────────────────────────────────────────
    private Task LoadBranches() => LoadBranchesPageAsync(1, 25);
    private Task ReloadBranchesAsync() => GitRepositoryGrids.ReloadOrLoadAsync(_grids.Branches, LoadBranches);
    // The grid scrolls and fetches further blocks; only the first one feeds the ref pickers, so
    // scrolling the branch list does not change what the Commits and Files tabs offer.
    private Task LoadBranchesPageAsync(int page, int pageSize, string? sortBy = null, bool sortDescending = false) => LoadPageAsync(
        _branchPage,
        () => Api.Git.GetGitBranchesPageAsync(Id, page, pageSize, BranchSearch, sortBy, sortDescending, filters: _grids.BranchFilters),
        items =>
        {
            _branchRows = items;
            if (page == 1) _branches = items;
        });
    private async Task OnLoadBranches(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(GitLightBranchDto.Name));
        _grids.BranchFilters = args.ToApiFilters();
        await LoadBranchesPageAsync(page, pageSize, sortBy, sortDescending);
    }
    private async Task ShowCreateBranchDialog()
    {
        if (await Actions.CreateBranchAsync(Id)) await ReloadBranchesAsync();
    }
    private async Task DeleteBranch(GitLightBranchDto branch)
    {
        if (await Actions.DeleteBranchAsync(Id, branch)) await ReloadBranchesAsync();
    }
    // ── Tags ─────────────────────────────────────────────────────────
    private Task LoadTags() => LoadTagsPageAsync(1, 25);
    private Task ReloadTagsAsync() => GitRepositoryGrids.ReloadOrLoadAsync(_grids.Tags, LoadTags);
    private Task LoadTagsPageAsync(int page, int pageSize, string? sortBy = null, bool sortDescending = false) => LoadPageAsync(
        _tagPage,
        () => Api.Git.GetGitTagsPageAsync(Id, page, pageSize, search: null, sortBy, sortDescending, filters: _grids.TagFilters),
        items => _tags = items);
    private async Task OnLoadTags(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(GitLightTagDto.Name));
        _grids.TagFilters = args.ToApiFilters();
        await LoadTagsPageAsync(page, pageSize, sortBy, sortDescending);
    }
    private async Task ShowCreateTagDialog()
    {
        if (await Actions.CreateTagAsync(Id)) await ReloadTagsAsync();
    }
    private async Task DeleteTag(GitLightTagDto tag)
    {
        if (await Actions.DeleteTagAsync(Id, tag)) await ReloadTagsAsync();
    }
    // ── File browser ─────────────────────────────────────────────────
    // The README is rendered to a MarkupString: the pipeline that builds it keeps raw HTML out.
    private static readonly Markdig.MarkdownPipeline _markdownPipeline =
        new Markdig.MarkdownPipelineBuilder().DisableHtml().Build();
    private async Task LoadReadme()
    {
        var generation = _dataGeneration;
        _readmeContent = _readmeHtml = null;
        var readme = await GitReadme.LoadAsync(Api.Git, Id, _treeRef ?? _repo!.DefaultBranch, _treeEntries, _markdownPipeline);
        if (generation == _dataGeneration) (_readmeContent, _readmeHtml) = readme;
    }
    // FRONT-003: the file tab's answers apply only while they are the latest request of the repository
    // still on screen; a late answer for another repository, folder or ref is dropped.
    private int _treeRequest, _blobRequest;
    private bool IsLatest(int generation, int request, int latest) => generation == _dataGeneration && request == latest;
    // FRONT-002: blame annotations belong to one blob at one ref; any change of file or ref drops them.
    private void ClearBlame()
    {
        _blameLines = null;
        _blameFormatted = null;
    }
    private async Task LoadTree()
    {
        // The file grid is only rendered when no file is open and the tree loaded; while it is,
        // another folder or ref reloads it from its first row through its own loader.
        var mountedGrid = _treeError || _blobContent is not null ? null : _grids.Tree;
        await DisposeMonacoAsync();
        _blobRequest++;
        _blobContent = null;
        ClearBlame();
        await GitRepositoryGrids.ReloadOrLoadAsync(mountedGrid,
            () => LoadTreePageAsync(() => Api.Git.GetGitTreePageAsync(Id, _treeRef, _treePath, filters: _grids.TreeFilters)));
    }
    private async Task OnLoadTree(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(GitLightTreeEntryDto.Name));
        _grids.TreeFilters = args.ToApiFilters();
        await LoadTreePageAsync(
            () => Api.Git.GetGitTreePageAsync(Id, _treeRef, _treePath, page, pageSize, search: null, sortBy, sortDescending, filters: _grids.TreeFilters));
    }
    private async Task LoadTreePageAsync(
        Func<Task<PaginatedResult<GitLightTreeEntryDto>>> loadAsync)
    {
        var generation = _dataGeneration;
        var request = ++_treeRequest;
        _treeLoading = true;
        _treeError = false;
        try
        {
            var result = await loadAsync();
            if (!IsLatest(generation, request, _treeRequest)) return;
            _treeEntries = result.Items;
            _treeTotalCount = result.TotalCount;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            if (!IsLatest(generation, request, _treeRequest)) return;
            _treeEntries = [];
            _treeTotalCount = 0;
            _treeError = true;
        }
        finally { if (IsLatest(generation, request, _treeRequest)) _treeLoading = false; }
    }
    private async Task NavigateToEntry(GitLightTreeEntryDto entry)
    {
        if (entry.Type == GitTreeEntryType.Tree)
        {
            _treePath = entry.Path;
            await LoadTree();
        }
        else
        {
            await DisposeMonacoAsync();
            var generation = _dataGeneration;
            var request = ++_blobRequest;
            ClearBlame();
            _treeLoading = true;
            _treeError = false;
            try
            {
                var blob = await Api.Git.GetGitBlobAsync(
                    Id, _treeRef ?? _repo!.DefaultBranch, entry.Path);
                if (!IsLatest(generation, request, _blobRequest)) return;
                _blobContent = blob;
                _pendingMonacoInit = _blobContent is not null && !_blobContent.IsBinary;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                if (!IsLatest(generation, request, _blobRequest)) return;
                _blobContent = null;
                _treeError = true;
            }
            finally { if (IsLatest(generation, request, _blobRequest)) _treeLoading = false; }
        }
    }

    private async Task OpenDeepLinkedFileAsync()
    {
        var path = FilePath?.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(path) || path.EndsWith('/')) return;
        await DisposeMonacoAsync();
        var generation = _dataGeneration;
        var request = ++_blobRequest;
        ClearBlame();
        _treePath = Path.GetDirectoryName(path)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(_treePath)) _treePath = null;
        var blob = await Api.Git.GetGitBlobAsync(Id, _treeRef ?? _repo!.DefaultBranch, path);
        if (!IsLatest(generation, request, _blobRequest)) return;
        _blobContent = blob;
        _pendingMonacoInit = _blobContent is not null && !_blobContent.IsBinary;
    }
    private async Task CloseBlobAsync()
    {
        await DisposeMonacoAsync();
        _blobRequest++;
        _blobContent = null;
        ClearBlame();
    }
    private async Task NavigateUp()
    {
        if (string.IsNullOrEmpty(_treePath)) return;
        var lastSlash = _treePath.LastIndexOf('/');
        _treePath = lastSlash > 0 ? _treePath[..lastSlash] : null;
        await LoadTree();
    }
    // ── Pull Requests ────────────────────────────────────────────────
    private Task ReloadPrsAsync() => GitRepositoryGrids.ReloadOrLoadAsync(_grids.PullRequests, LoadPrs);
    private Task LoadPrs() =>
        _pullRequests.LoadAsync(() => Api.Git.GetGitPullRequestsAsync(Id, filters: _grids.PullRequestFilters));
    private Task OnLoadPrs(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(InternalPullRequestDto.CreatedAt), fallbackDescending: true);
        _grids.PullRequestFilters = args.ToApiFilters();
        // S-TECH-SWGT: SWR for the pull-requests grid - instant paint from cache, then refresh.
        var key = $"{PrsCacheKey(page, pageSize, sortBy, sortDescending)}:{GridColumnFilters.CacheText(_grids.PullRequestFilters)}";
        return _pullRequests.LoadCachedAsync(Cache, key,
            () => Api.Git.GetGitPullRequestsAsync(Id, page, pageSize, search: null, status: null, sortBy, sortDescending, _grids.PullRequestFilters));
    }
    private async Task ShowCreatePrDialog()
    {
        if (await Actions.CreatePullRequestAsync(Id)) await ReloadPrsAsync();
    }
    private async Task MergePr(InternalPullRequestDto pr)
    {
        if (await Actions.MergePullRequestAsync(Id, pr)) await ReloadPrsAsync();
    }
    private async Task ClosePr(InternalPullRequestDto pr)
    {
        if (await Actions.ClosePullRequestAsync(Id, pr)) await ReloadPrsAsync();
    }
    // ── Commit Graph ───────────────────────────────────────────────
    private async Task LoadGraph()
    {
        var generation = _dataGeneration;
        _graphLoading = true;
        _graphError = false;
        try
        {
            var commits = await Api.Git.GetGitGraphDataAsync(Id);
            if (generation == _dataGeneration) _graphCommits = commits;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            if (generation != _dataGeneration) return;
            _graphCommits = [];
            _graphError = true;
        }
        finally { if (generation == _dataGeneration) _graphLoading = false; }
    }
    // ── Branch Protection ───────────────────────────────────────────
    private Task LoadProtectionRules() => LoadProtectionRulesPageAsync(1, 25);
    private Task ReloadProtectionRulesAsync() => GitRepositoryGrids.ReloadOrLoadAsync(_grids.Protection, LoadProtectionRules);
    // The grid scrolls and fetches further blocks; the branch lock icons read the first one only,
    // so scrolling the rules does not change which branches read as protected.
    private Task LoadProtectionRulesPageAsync(int page, int pageSize, string? sortBy = null, bool sortDescending = false) => LoadPageAsync(
        _protectionPage,
        () => Api.Git.GetGitBranchProtectionRulesPageAsync(Id, page, pageSize, search: null, sortBy, sortDescending, filters: _grids.ProtectionFilters),
        items =>
        {
            _protectionRows = items;
            if (page == 1) _protectionRules = items;
        });
    private async Task OnLoadProtectionRules(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(BranchProtectionRuleDto.Pattern));
        _grids.ProtectionFilters = args.ToApiFilters();
        await LoadProtectionRulesPageAsync(page, pageSize, sortBy, sortDescending);
    }
    private async Task ShowAddProtectionRuleDialog()
    {
        if (await Actions.AddProtectionRuleAsync(Id)) await ReloadProtectionRulesAsync();
    }
    private async Task DeleteProtectionRule(BranchProtectionRuleDto rule)
    {
        if (await Actions.DeleteProtectionRuleAsync(Id, rule)) await ReloadProtectionRulesAsync();
    }
    private async Task UpdateProtectionRule(BranchProtectionRuleDto rule, bool? preventDeletion = null, bool? preventForcePush = null, BranchProtectionLevel? protectionLevel = null)
    {
        if (await Actions.UpdateProtectionRuleAsync(
            Id, rule, preventDeletion, preventForcePush, protectionLevel))
            await ReloadProtectionRulesAsync();
    }
    private bool IsBranchProtected(string branchName)
    {
        return _protectionRules.Any(r => GitRepositoryViewHelpers.BranchMatchesPattern(branchName, r.Pattern));
    }
    // ── Blame ───────────────────────────────────────────────────────
    private async Task ToggleBlame()
    {
        if (_blameLines is not null)
        {
            _blameLines = null;
            _blameFormatted = null;
            _pendingMonacoInit = true;
            return;
        }
        if (_blobContent is null) return;
        var generation = _dataGeneration;
        var request = _blobRequest;
        var blame = await Api.Git.GetGitBlameAsync(Id, _treeRef ?? _repo!.DefaultBranch, _blobContent.Path);
        // Only the still-open file may receive its loading result.
        if (!IsLatest(generation, request, _blobRequest)) return;
        _blameLines = blame;
        if (_blameLines.Count > 0)
        {
            _blameFormatted = GitRepositoryViewHelpers.FormatBlame(_blameLines);
            await JS.InvokeVoidAsync("monacoInterop.disposeEditor", _monacoElementId);
            _monacoInitialized = false;
        }
        else
        {
            _blameLines = null;
        }
    }
    private static OmniTone GetPrStatusBadge(PullRequestStatus status) => GitRepositoryViewHelpers.GetPullRequestStatusBadge(status);
    // The repository as a zip (default branch), streamed by the API and handed to the browser.
    private async Task DownloadArchiveAsync()
    {
        if (_repo is null || _downloadingArchive) return;
        _downloadingArchive = true;
        try
        {
            var archive = await Api.Git.DownloadGitArchiveAsync(Id);
            if (archive is null)
            {
                Toast.Error("DownloadFailed");
                return;
            }
            using var streamRef = new DotNetStreamReference(archive.Value.Content);
            await JS.InvokeVoidAsync("downloadFileFromStream", archive.Value.FileName, streamRef);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Could not download the zip of Git repository {RepositoryId}.", Id);
            Toast.Error("DownloadFailed");
        }
        finally { _downloadingArchive = false; }
    }
    private async Task ToggleConfigurationAsync()
    {
        if (_repo is null || !_canWrite) return;
        if (await Actions.SetConfigurationAsync(Id, !_repo.IsConfiguration)) await LoadData();
    }
    /// <summary> The clone URL field copies itself (OE's Copyable) and its button shows the check;
    /// a copy the browser refused shows nothing visible on the button, so it gets the app's
    /// warning. </summary>
    private void OnCloneUrlCopied(bool copied)
    {
        if (!copied)
            Toast.Warning("CopyFailed", "ClipboardUnavailable");
    }
    private async Task ConnectHub()
    {
        _realtime = new GitRepositoryRealtimeSubscription(HubFactory);
        await _realtime.StartAsync(
            Id,
            // A live event refreshes the grid in place (page, sort and filters kept).
            () => InvokeAsync(async () => { await GitRepositoryGrids.RefreshOrLoadAsync(_grids.Branches, LoadBranches); StateHasChanged(); }),
            () => InvokeAsync(async () => { await GitRepositoryGrids.RefreshOrLoadAsync(_grids.Tags, LoadTags); StateHasChanged(); }),
            () => InvokeAsync(async () => { Cache.InvalidatePrefix($"git-commits:{Id}:"); await GitRepositoryGrids.RefreshOrLoadAsync(MountedCommitsGrid, LoadCommits); StateHasChanged(); }),
            () => InvokeAsync(async () => { Cache.InvalidatePrefix($"git-prs:{Id}:"); await GitRepositoryGrids.RefreshOrLoadAsync(_grids.PullRequests, LoadPrs); StateHasChanged(); }),
            () => InvokeAsync(async () => { _repo = await Api.Git.GetGitRepoAsync(Id); StateHasChanged(); }));
    }
}
