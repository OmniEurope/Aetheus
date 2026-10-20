// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Markdig;

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public partial class ProjectOverviewSection : IAsyncDisposable
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private IUserPreferences Preferences { get; set; } = default!;

    [Parameter, EditorRequired] public ProjectDetailDto? Project { get; set; }

    private const int RecentItemLimit = 5;
    private List<PipelineRunDto> _recentPipelineRuns = [];
    private List<RecentCommitView> _recentCommits = [];
    private bool _loadingRecentPipelines = true;
    private bool _loadingRecentCommits = true;
    private bool _gitStateLoaded;
    private bool _hasGitRepository;
    private GitLightRepoDto? _primaryGitRepository;
    private HubConnection? _pipelineHub;
    private readonly TrailingReloadCoalescer _pipelineReload = new(500);

    // F-ter: README (rendered from the project's git repo, same Markdig pipeline as GitRepositoryDetail)
    // and the release changelog timeline. Their tabs stay visible and show an honest empty state.
    private string? _readmeHtml;
    private string? _readmeBranch;
    private List<ReleaseDto> _changelog = [];
    private int? _loadedProjectId;

    /// <summary>Libraries and vaults this project's pipelines require and it lacks.</summary>
    private List<UnmetRequirementDto> _unmet = [];
    private bool _provisioningUnmet;
    private string? _unmetError;

    private sealed record RecentCommitView(
        int RepositoryId,
        string RepositoryName,
        string Sha,
        string ShortSha,
        string BranchName,
        string Message,
        string AuthorName,
        DateTime AuthorDate)
    {
        public static RecentCommitView FromCommit(GitLightRepoDto repository, GitLightCommitDto commit)
        {
            var branchRefs = commit.RefNames
                .Where(reference => !reference.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                .Select(reference => reference.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
                    ? reference["origin/".Length..]
                    : reference)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new(
                repository.Id,
                repository.Name,
                commit.Sha,
                DisplayFormatting.ShortSha(commit.Sha),
                branchRefs.Count == 0 ? "-" : string.Join(", ", branchRefs),
                commit.Message,
                commit.AuthorName,
                commit.AuthorDate);
        }
    }

    private static readonly string[] TabSlugs = ["overview", "readme", "changelog"];

    // F-bis: "Métriques · dernière release" - coverage (CoverageSummary) and LOC (RunMetric "loc.total")
    // of the run that produced the project's most recent release. Null when the latest release has no
    // linked run, or that run carries neither metric - never a fabricated value.
    private LastReleaseMetricsView? _lastReleaseMetrics;

    private sealed record LastReleaseMetricsView(
        string Version, DateTime When, double? CoverageLineRate, int? LinesOfCode);

    // Coverage threshold colouring, consistent with the run view (>=80% green, >=50% amber, else red).
    private static string CoverageColorClass(double rate) => rate switch
    {
        >= 0.8 => "omni-u-text-success",
        >= 0.5 => "omni-u-text-warning",
        _ => "omni-u-text-danger"
    };

    // Mirrors GitRepositoryDetail: HTML disabled so untrusted README markup can't inject script.
    private static readonly MarkdownPipeline _markdownPipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    /// <summary>F: last-run status across the project's pipelines (most recent run), for the tile dot.</summary>
    private PipelineStatus? LatestRunStatus =>
        _recentPipelineRuns.FirstOrDefault()?.Status
        ?? Project?.Pipelines
            .Where(p => p.LastRunAt.HasValue && p.LastRunStatus.HasValue)
            .OrderByDescending(p => p.LastRunAt)
            .Select(p => p.LastRunStatus)
            .FirstOrDefault();

    /// <summary>
    /// The checklist lives until the project can actually run something: a repository to build, an
    /// environment to target, a server to run on, and a pipeline to run. A variable library is a
    /// convenience rather than a prerequisite, so its row is shown while the checklist is up but
    /// never keeps it up on its own, otherwise a project that legitimately shares no variables
    /// would carry a permanent "getting started" card.
    /// </summary>
    private bool ShowGettingStarted => Project is not null && _gitStateLoaded
        && (!GettingStartedComplete || _gettingStartedLastShow);

    private bool GettingStartedComplete => Project is not null
        && _hasGitRepository && Project.EnvironmentCount > 0 && HasServer && Project.Pipelines.Count > 0;

    /// <summary>
    /// Recette R3-67: a finished checklist is shown one last time, saying so, then never again for
    /// this user and project (the step that was missing must not vanish unseen).
    /// </summary>
    private bool _gettingStartedLastShow;

    private string GettingStartedDescription => GettingStartedComplete
        ? L["ProjectGettingStartedDoneDescription"]
        : L["ProjectGettingStartedDescription"];

    private async Task ResolveGettingStartedLastShowAsync(int projectId)
    {
        _gettingStartedLastShow = false;
        if (!_gitStateLoaded || !GettingStartedComplete) return;
        if (await Preferences.GetAsync<bool>(PreferenceKeys.GettingStartedClosed(projectId))) return;

        _gettingStartedLastShow = true;
        await Preferences.SetAsync(PreferenceKeys.GettingStartedClosed(projectId), true);
    }

    /// <summary>Runner availability includes servers attached directly or through project
    /// environments.</summary>
    private bool HasServer => Project is not null
        && (Project.ServerCount > 0 || Project.EnvironmentServerCount > 0);

    private string GitOnboardingHref => _hasGitRepository
        ? $"/git-repositories?projectId={Project!.Id}"
        : $"/git-repositories?projectId={Project!.Id}&create=true";

    private string EnvironmentOnboardingHref => Project!.EnvironmentCount > 0
        ? $"/projects/{Project.Id}/environments"
        : $"/environments/new?projectId={Project.Id}";

    // Attaching a server to a project happens in the project's own Servers section, so both the
    // done and the to-do action land there; installing a brand-new agent is one click further.
    private string ServerOnboardingHref => $"/projects/{Project!.Id}/servers";

    private string LibraryOnboardingHref => Project!.LibraryCount > 0
        ? $"/projects/{Project.Id}/libraries"
        : $"/variable-libraries/new?projectId={Project.Id}";

    private string PipelineOnboardingHref => Project!.Pipelines.Count > 0
        ? $"/projects/{Project.Id}/pipelines"
        : $"/pipelines/setup?projectId={Project.Id}";

    /// <summary>
    /// Project-scoped onboarding rows. Every state comes from data already loaded for this page:
    /// the repository list fetched by <see cref="LoadGitDataAsync"/> and the project's own counters.
    /// The project exists by construction here, so no row can be blocked.
    /// </summary>
    private IReadOnlyList<OnboardingStep> GettingStartedSteps =>
    [
        new OnboardingStep
        {
            Title = L["CreateGitRepository"],
            Description = L["CreateGitRepositoryHint"],
            Icon = "commit",
            State = _hasGitRepository ? OnboardingStepState.Done : OnboardingStepState.Todo,
            Href = GitOnboardingHref,
            ActionLabel = _hasGitRepository ? L["GitRepositories"] : L["CreateGitRepository"]
        },
        new OnboardingStep
        {
            Title = L["CreateEnvironment"],
            Description = L["CreateEnvironmentHint"],
            Icon = "layers",
            State = Project!.EnvironmentCount > 0 ? OnboardingStepState.Done : OnboardingStepState.Todo,
            Href = EnvironmentOnboardingHref,
            ActionLabel = Project.EnvironmentCount > 0 ? L["Environments"] : L["CreateEnvironment"]
        },
        new OnboardingStep
        {
            Title = L["AttachFirstServer"],
            Description = L["AttachFirstServerHint"],
            Icon = "dns",
            State = HasServer ? OnboardingStepState.Done : OnboardingStepState.Todo,
            Href = ServerOnboardingHref,
            ActionLabel = HasServer ? L["Servers"] : L["AddServer"]
        },
        new OnboardingStep
        {
            Title = L["CreatePipeline"],
            Description = L["CreatePipelineHint"],
            Icon = "account_tree",
            State = Project.Pipelines.Count > 0 ? OnboardingStepState.Done : OnboardingStepState.Todo,
            Href = PipelineOnboardingHref,
            ActionLabel = Project.Pipelines.Count > 0 ? L["Pipelines"] : L["CreatePipeline"]
        },
        .. LibraryStep
    ];

    /// <summary>Library setup follows pipeline requirements: missing resources are actionable,
    /// complete requirements are done and unnecessary setup stays hidden.</summary>
    private IEnumerable<OnboardingStep> LibraryStep
    {
        get
        {
            var missing = _unmet.Count > 0;
            if (!missing && Project!.LibraryCount == 0) yield break;
            yield return new OnboardingStep
            {
                Title = L["CreateVariableLibrary"],
                Description = L["CreateVariableLibraryHint"],
                Icon = "library_books",
                State = missing ? OnboardingStepState.Todo : OnboardingStepState.Done,
                Href = LibraryOnboardingHref,
                ActionLabel = missing ? L["CreateVariableLibrary"] : L["Libraries"]
            };
        }
    }

    private void AddGitRepository()
    {
        if (Project is not null)
            Nav.NavigateTo($"/git-repositories?projectId={Project.Id}&create=true");
    }

    private string? RepositoryDisplayUrl => !string.IsNullOrWhiteSpace(Project?.RepositoryUrl)
        ? Project.RepositoryUrl
        : _primaryGitRepository?.CloneUrl;

    private string? RepositoryDisplayBranch => !string.IsNullOrWhiteSpace(Project?.DefaultBranch)
        ? Project.DefaultBranch
        : _primaryGitRepository?.DefaultBranch;

    /// <summary>Branch and latest activity share a row built only from available values, avoiding
    /// dangling separators.</summary>
    private string RepositoryActivityLabel => JoinHalves(
        string.IsNullOrWhiteSpace(RepositoryDisplayBranch) ? null : L["DefaultBranch"].Value,
        Project?.LastGitUpdateAt is null ? null : L["LastGitUpdate"].Value);

    private string RepositoryActivityValue => JoinHalves(
        RepositoryDisplayBranch,
        Project?.LastGitUpdateAt is { } at ? RelativeTime.FormatAgo(L, at) : null);

    private static string JoinHalves(string? first, string? second) =>
        string.Join(" · ", new[] { first, second }.Where(part => !string.IsNullOrWhiteSpace(part)));

    protected override async Task OnInitializedAsync()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        await StartPipelineHubAsync();
    }

    private void OnPermissionsChanged() => InvokeAsync(StateHasChanged);

    protected override async Task OnParametersSetAsync()
    {
        if (Project is null || _loadedProjectId == Project.Id) return;
        _loadedProjectId = Project.Id;
        _recentPipelineRuns = [];
        _recentCommits = [];
        _loadingRecentPipelines = true;
        _loadingRecentCommits = true;
        _gitStateLoaded = false;
        _hasGitRepository = false;
        _primaryGitRepository = null;
        _readmeHtml = null;
        _readmeBranch = null;
        _changelog = [];
        _lastReleaseMetrics = null;

        _unmet = [];
        _unmetError = null;

        await Task.WhenAll(
            LoadRecentPipelinesAsync(Project.Id),
            LoadReleaseDataAsync(Project.Id),
            LoadGitDataAsync(Project.Id),
            LoadUnmetRequirementsAsync(Project.Id));
        await ResolveGettingStartedLastShowAsync(Project.Id);
    }

    private async Task LoadUnmetRequirementsAsync(int projectId)
    {
        try
        {
            _unmet = await Api.Pipelines.GetUnmetRequirementsAsync(projectId);
        }
        catch (HttpRequestException)
        {
            // Not knowing is not the same as nothing missing, but a warning must not be invented
            // either: the card simply shows no warning, and the launch preflight still refuses.
            _unmet = [];
        }
        _unmetDismissed = await ReadUnmetDismissalAsync(projectId) == UnmetSignature(_unmet);
    }

    /// <summary>The warning dismissed for this project and this exact list of missing resources; a
    /// list that changes shows it again.</summary>
    private bool _unmetDismissed;

    internal static string UnmetSignature(IEnumerable<UnmetRequirementDto> unmet) => string.Join("|",
        unmet.Select(item => $"{item.Kind}:{item.Name}").Order(StringComparer.Ordinal));

    // R1-66: the dismissal is a user preference, so it follows the user to their other devices.
    private Task<string?> ReadUnmetDismissalAsync(int projectId) =>
        Preferences.GetAsync<string>(PreferenceKeys.UnmetDismissed(projectId));

    private async Task DismissUnmetAsync()
    {
        if (Project is null) return;
        _unmetDismissed = true;
        await Preferences.SetAsync(PreferenceKeys.UnmetDismissed(Project.Id), UnmetSignature(_unmet));
    }

    /// <summary>The one-click fix from the Information card, then a re-check so the warning
    /// disappears as soon as the resources exist.</summary>
    private async Task ProvisionUnmetAsync()
    {
        if (Project is null) return;
        _provisioningUnmet = true;
        _unmetError = null;
        try
        {
            var result = await Api.Pipelines.ProvisionUnmetRequirementsAsync(Project.Id);
            if (result is null)
            {
                _unmetError = L["PipelineSetupProvisionFailed"].Value;
                return;
            }

            await LoadUnmetRequirementsAsync(Project.Id);
        }
        catch (HttpRequestException)
        {
            _unmetError = L["PipelineSetupProvisionFailed"].Value;
        }
        finally
        {
            _provisioningUnmet = false;
        }
    }

    private async Task LoadRecentPipelinesAsync(int projectId)
    {
        try
        {
            _recentPipelineRuns = (await Api.Pipelines.GetRecentPipelineRunsAsync(projectId))
                .OrderByDescending(run => run.StartedAt)
                .ToList();
        }
        catch
        {
            _recentPipelineRuns = [];
        }
        finally { _loadingRecentPipelines = false; }
    }

    private async Task StartPipelineHubAsync()
    {
        try
        {
            _pipelineHub = HubFactory.Create("pipelines");
            _pipelineHub.On<int, int>("PipelineRunStarted", OnPipelineRunStarted);
            _pipelineHub.On<int, PipelineStatus>("PipelineRunCompleted", (runId, _) => OnPipelineRunChangedAsync(runId));
            _pipelineHub.On<int>("PipelineRunCancelled", OnPipelineRunChangedAsync);
            _pipelineHub.On<int, string, string>("ApprovalRequired", (runId, _, _) => OnPipelineRunChangedAsync(runId));
            _pipelineHub.On<int, string, string>("ApprovalResolved", (runId, _, _) => OnPipelineRunChangedAsync(runId));
            _pipelineHub.RejoinOnReconnect(async () =>
            {
                await _pipelineHub.InvokeAsync("JoinPipelineUpdatesGroup");
                await ReloadRecentPipelinesAsync();
            });
            await _pipelineHub.StartAsync();
            await _pipelineHub.InvokeAsync("JoinPipelineUpdatesGroup");
        }
        catch { /* best-effort: the overview remains usable without live updates */ }
    }

    private Task OnPipelineRunStarted(int runId, int pipelineId) =>
        Project?.Pipelines.Any(pipeline => pipeline.Id == pipelineId) == true
            ? RequestRecentPipelineReloadAsync()
            : Task.CompletedTask;

    private async Task OnPipelineRunChangedAsync(int runId)
    {
        if (Project is null) return;
        try
        {
            var run = await Api.Pipelines.GetPipelineRunAsync(runId);
            if (run?.ProjectId == Project.Id) await RequestRecentPipelineReloadAsync();
        }
        catch (HttpRequestException) { } // hub-triggered reload - silent on auth failure
    }

    private Task RequestRecentPipelineReloadAsync() =>
        InvokeAsync(() => _pipelineReload.RequestAsync(() => InvokeAsync(ReloadRecentPipelinesAsync)));

    private async Task ReloadRecentPipelinesAsync()
    {
        if (Project is null) return;
        await LoadRecentPipelinesAsync(Project.Id);
        StateHasChanged();
    }

    private async Task LoadGitDataAsync(int projectId)
    {
        List<GitLightRepoDto> repositories;
        try
        {
            repositories = await Api.Git.GetGitReposAsync(projectId);
        }
        catch
        {
            _recentCommits = [];
            _loadingRecentCommits = false;
            _gitStateLoaded = false;
            return;
        }

        _hasGitRepository = repositories.Count > 0 || !string.IsNullOrWhiteSpace(Project?.RepositoryUrl);
        _primaryGitRepository = repositories.FirstOrDefault(repository => !repository.IsEmpty)
            ?? repositories.FirstOrDefault();
        _gitStateLoaded = true;

        var commitTasks = repositories
            .Where(repository => !repository.IsEmpty)
            .Select(LoadRecentCommitsAsync);
        var readmeRepository = _primaryGitRepository;

        var commitsTask = Task.WhenAll(commitTasks);
        var readmeTask = readmeRepository is null ? Task.CompletedTask : LoadReadmeAsync(readmeRepository);
        var commitGroups = await commitsTask;
        _recentCommits = commitGroups
            .SelectMany(commits => commits)
            .OrderByDescending(commit => commit.AuthorDate)
            .Take(RecentItemLimit)
            .ToList();
        _loadingRecentCommits = false;
        await readmeTask;
    }

    private async Task<List<RecentCommitView>> LoadRecentCommitsAsync(GitLightRepoDto repository)
    {
        try
        {
            var result = await Api.Git.GetGitCommitsAsync(
                repository.Id,
                GitReference.AllBranches,
                pageSize: RecentItemLimit);
            return result.Items
                .Select(commit => RecentCommitView.FromCommit(repository, commit))
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>Loads the project's releases once, then derives the changelog timeline and the
    /// "last release" metrics panel from the same list (one round-trip).</summary>
    private async Task LoadReleaseDataAsync(int projectId)
    {
        List<ReleaseDto> releases;
        try
        {
            var result = await Api.Projects.GetReleasesAsync(projectId: projectId);
            releases = result.Items;
        }
        // Best-effort enrichment (same isolation as GitRepositoryDetail's loaders): an expired
        // JWT (401), a transport error, or an unexpected payload must not collapse the overview.
        catch { _changelog = []; return; }

        _changelog = releases
            .Where(r => !string.IsNullOrWhiteSpace(r.Changelog))
            .OrderByDescending(r => r.PublishedAt ?? r.DetectedAt)
            .ToList();

        await LoadLastReleaseMetricsAsync(releases);
    }

    /// <summary>F-bis: fetches the run behind the most recent release and surfaces its coverage + LOC.
    /// Coverage comes from <c>CoverageSummary</c>; LOC from the <c>loc.total</c> run metric (published by a
    /// complexity step). The panel is shown only when at least one real metric exists.</summary>
    private async Task LoadLastReleaseMetricsAsync(List<ReleaseDto> releases)
    {
        var latest = releases
            .OrderByDescending(r => r.PublishedAt ?? r.DetectedAt)
            .FirstOrDefault(r => r.PipelineRunId.HasValue);
        if (latest?.PipelineRunId is not { } runId) return;

        PipelineRunDto? run;
        try { run = await Api.Pipelines.GetPipelineRunAsync(runId); }
        catch (HttpRequestException) { return; }
        if (run is null) return;

        var coverage = run.CoverageSummary?.LineRate;
        var locMetric = run.Metrics.FirstOrDefault(m => m.Key == "loc.total");
        int? loc = locMetric is null ? null : (int)Math.Round(locMetric.Value);

        if (coverage is null && loc is null) return; // nothing real to show
        _lastReleaseMetrics = new LastReleaseMetricsView(
            latest.Version, latest.PublishedAt ?? latest.DetectedAt, coverage, loc);
    }

    /// <summary>
    /// Renders the README of the project's first git repository (HEAD of its default branch),
    /// reusing the exact Markdig pipeline + sanitization from GitRepositoryDetail. No fabrication:
    /// when the project has no internal git repo, or the repo has no README.md, the README tab
    /// retains its explicit empty state.
    /// </summary>
    private async Task LoadReadmeAsync(GitLightRepoDto repo)
    {
        try
        {
            var branch = repo.DefaultBranch;
            var tree = await Api.Git.GetGitTreeAsync(repo.Id, branch);
            var readme = tree.FirstOrDefault(e =>
                e.Type == GitTreeEntryType.Blob &&
                e.Name.Equals("README.md", StringComparison.OrdinalIgnoreCase));
            if (readme is null) return;

            var blob = await Api.Git.GetGitBlobAsync(repo.Id, branch, readme.Path);
            if (blob is null || blob.IsBinary || string.IsNullOrWhiteSpace(blob.Content)) return;

            _readmeHtml = Markdown.ToHtml(blob.Content, _markdownPipeline);
            _readmeBranch = branch;
        }
        // Best-effort: no internal git repo / no README / 401 / transport error → explicit empty state.
        catch { _readmeHtml = null; }
    }

    private static string CommitHref(RecentCommitView commit) =>
        $"/git-repositories/{commit.RepositoryId}/commits/{Uri.EscapeDataString(commit.Sha)}";

    private void OnRecentCommitClick(RecentCommitView commit) => Nav.NavigateTo(CommitHref(commit));

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        if (_pipelineHub is not null)
        {
            try { await _pipelineHub.InvokeAsync("LeavePipelineUpdatesGroup"); } catch { /* best-effort */ }
            await _pipelineHub.DisposeAsync();
        }
    }
}
