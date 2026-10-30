// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Git;
using Aetheus.Front.Components.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aetheus.Front.Components.Releases;

public partial class ReleaseDetail : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ILogger<ReleaseDetail> Logger { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private PipelineRunGate RunGate { get; set; } = default!;
    [Inject] private PipelineRunDialogCoordinator RunDialogs { get; set; } = default!;

    [Parameter] public int ReleaseId { get; set; }

    private ReleaseDto? _release;
    private int? _loadedReleaseId;
    private int _loadGeneration;
    private bool _loading;
    private HubConnection? _hubConnection;
    private ReleaseRollbackPreviewDto? _rollbackPreview;
    private ReleaseProvenanceDto? _provenance;
    private bool _canWrite;
    private List<string> _linkedCommitMessages = [];

    /// <summary>R-31: copies the release's full name, shown in the Source tile.</summary>
    private Task CopyVersionAsync() => Clipboard.CopyAsync(_release!.Version, _release.Version);

    /// <summary>R-31: copies the release's tag, shown in the Source tile.</summary>
    private Task CopyTagAsync() => Clipboard.CopyAsync(_release!.TagName!, _release.TagName!);

    // The Deliverables panel groups what the release ships (artifacts) and where it targets
    // (environment). Hide it when the release has neither, so we never render an empty panel.
    private bool HasDeliverables =>
        _release is not null &&
        (_release.Artifacts.Count > 0 || !string.IsNullOrEmpty(_release.EnvironmentName));
    /// <summary>The repository holding the release's bare commit hash, resolved from its repository
    /// URL; null when unknown.</summary>
    private int? _repositoryId;

    /// <summary>A bare commit hash leads to the commit's own page when its repository is known; a
    /// release whose repository cannot be told shows it as text (null), never a list of
    /// repositories to search in.</summary>
    private string? BareCommitHref => _repositoryId is { } repositoryId && !string.IsNullOrWhiteSpace(_release?.CommitHash)
        ? $"/git-repositories/{repositoryId}/commits/{Uri.EscapeDataString(_release.CommitHash)}"
        : null;

    /// <summary>The tag points at the release's commit: the linked commit whose SHA is the release's
    /// (or the only linked one), else the release's commit in its repository.</summary>
    private string? TagHref => _release is not null && LinkedReleaseCommit(_release) is { } commit
        ? $"/git-repositories/commits/{commit.Id}"
        : BareCommitHref;

    private static CommitLinkDto? LinkedReleaseCommit(ReleaseDto release) =>
        release.Commits.FirstOrDefault(c => string.Equals(c.Sha, release.CommitHash, StringComparison.OrdinalIgnoreCase))
        ?? (release.Commits.Count == 1 ? release.Commits[0] : null);

    /// <summary> The repository of a release that carries its commit hash but no linked commit: the
    /// project's repository its URL names (by project and slug for an internal clone URL, whatever
    /// host it was re-homed to), or the project's only repository when the release names none.
    /// Never guessed among several. </summary>
    private async Task<int?> ResolveRepositoryAsync(ReleaseDto release)
    {
        if (string.IsNullOrWhiteSpace(release.CommitHash) || LinkedReleaseCommit(release) is not null) return null;
        List<GitLightRepoDto> repositories;
        try { repositories = await Api.Git.GetGitReposAsync(release.ProjectId); }
        catch (HttpRequestException) { return null; }

        return GitRepositoryUrl.ResolveRepositoryId(release.RepositoryUrl, repositories);
    }

    private bool CanRollback => _rollbackPreview?.CanRollback == true;
    /// <summary>R-11: rolling back is a permission of its own (release.rollback); the backend also asks Project.Write.</summary>
    private bool CanRollbackRelease => _release is not null && Permissions.Can(PermissionKeys.ReleaseRollback, _release.Id);
    private string RollbackDisabledReason => !CanRollbackRelease ? L["InsufficientPermissions"]
        : _rollbackPreview?.Reason ?? L["RollbackUnavailable"];

    protected override void OnInitialized()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        _canWrite = Permissions.CanWrite(ResourceType.Release);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedReleaseId == ReleaseId) return;
        _loadedReleaseId = ReleaseId;
        var releaseId = ReleaseId;
        var generation = ++_loadGeneration;
        bool IsCurrent() => generation == _loadGeneration && ReleaseId == releaseId;
        _loading = true;
        await StopHubAsync();
        if (!IsCurrent()) return;
        _release = null;
        _rollbackPreview = null;
        _provenance = null;
        _repositoryId = null;
        _linkedCommitMessages = [];
        try
        {
            ReleaseDto? release;
            try { release = await Api.Projects.GetReleaseAsync(releaseId); }
            catch (HttpRequestException) { release = null; }
            if (!IsCurrent()) return;

            _release = release;
            if (release is not null)
            {
                await LoadLinkedCommitMessagesAsync(release);
                if (!IsCurrent()) return;
                ProjectNav.Set(release.ProjectId);
                ReassertBreadcrumb(release);
                var rollbackPreview = await Api.Projects.GetRollbackPreviewAsync(releaseId);
                if (!IsCurrent()) return;
                _rollbackPreview = rollbackPreview;
                var provenance = await LoadProvenanceAsync(releaseId);
                if (!IsCurrent()) return;
                _provenance = provenance;
                var repositoryId = await ResolveRepositoryAsync(release);
                if (!IsCurrent()) return;
                _repositoryId = repositoryId;
                await StartHubAsync(release.ProjectId);
                if (!IsCurrent()) await StopHubAsync();
            }
        }
        finally
        {
            if (IsCurrent()) _loading = false;
        }
    }

    // The provenance enriches the page; the release stays readable without it.
    private async Task<ReleaseProvenanceDto?> LoadProvenanceAsync(int releaseId)
    {
        try { return await Api.Projects.GetReleaseProvenanceAsync(releaseId); }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Release {ReleaseId} provenance could not be loaded.", releaseId);
            return null;
        }
    }

    private void OnPermissionsChanged()
    {
        _canWrite = Permissions.CanWrite(ResourceType.Release);
        InvokeAsync(StateHasChanged);
    }

    /// <summary>Decision D2: an archived release offers the relaunch of a complete pipeline from its commit.</summary>
    private bool IsArchived => _release?.Status == ReleaseStatus.Archived;
    private bool CanRelaunch => _release is not null && ReleaseRelaunch.CanRelaunch(_release.Status, _release.CommitHash);
    private string RelaunchTitle => !_canWrite ? L["InsufficientPermissions"]
        : CanRelaunch ? L["RelaunchFromCommit"] : L["RelaunchFromCommitUnavailable"];

    private async Task RelaunchFromCommitAsync()
    {
        if (_release is null) return;
        await new ArchivedReleaseRelauncher(Api, Dialog, Toast, Nav, L).RelaunchAsync(_release);
    }

    /// <summary>R-72: any release is deployed again from its page through the project's deployment pipeline.</summary>
    private bool RedeployOffered => _release is not null && ReleaseRedeploy.IsOffered(_release.Status);
    private bool CanRedeploy => _release is not null && _canWrite && ReleaseRedeploy.CanRedeploy(_release);
    private string RedeployTitle => !_canWrite ? L["InsufficientPermissions"]
        : _release is not null && ReleaseRedeploy.CanRedeploy(_release) ? L["Redeploy"] : L["RedeployNoArtifact"];

    private async Task RedeployAsync()
    {
        if (_release is null) return;
        await new ReleaseRedeployer(Api, RunGate, RunDialogs, Dialog, Toast, Nav, L).RedeployAsync(_release);
    }

    private async Task RollbackAsync()
    {
        if (_release is null || _rollbackPreview is not { CanRollback: true, TargetVersion: { } targetVersion }) return;
        var pipelines = await Api.Pipelines.GetPipelinesAsync(pageSize: 100, projectId: _release.ProjectId);
        var request = await Dialog.OpenAsync<RollbackReleaseDialog>(L["Rollback"].Value,
            new Dictionary<string, object?>
            {
                ["SourceVersion"] = _release.Version,
                ["TargetVersion"] = targetVersion,
                ["DeploymentAge"] = RelativeTime.FormatAgo(L, _release.PublishedAt ?? _release.DetectedAt),
                ["ProjectId"] = _release.ProjectId,
                ["Pipelines"] = pipelines.Items
            }, new OmniDialogOptions { Width = "520px", AutoFocusFirstElement = false });
        if (request is not RollbackReleaseRequest rollbackRequest) return;
        if (await Api.Projects.RollbackReleaseAsync(_release.Id, rollbackRequest) is not null)
            Toast.Success("RollbackQueued", _release.Version);
    }

    /// <summary>Recette R-13: the backend asks release.status.restore (R-11) and Project.Admin; an archived release only
    /// comes back through a new publication.</summary>
    private bool CanRestoreStatus => _release is not null && Permissions.Can(PermissionKeys.ReleaseStatusRestore, _release.Id)
        && ReleaseStatusRestore.CanRestoreFrom(_release.Status)
        && Permissions.CanAdmin(ResourceType.Project, _release.ProjectId);

    private async Task RestoreStatusAsync()
    {
        if (_release is null) return;
        var answer = await Dialog.OpenAsync<RestoreReleaseStatusDialog>(L["ReleaseStatusRestore"].Value,
            new Dictionary<string, object?>
            {
                ["Version"] = _release.Version,
                ["CurrentStatus"] = _release.Status
            }, new OmniDialogOptions { Width = "520px", AutoFocusFirstElement = false });
        if (answer is not RestoreReleaseStatusRequest request) return;
        var outcome = await Api.Projects.RestoreReleaseStatusAsync(_release.Id, request);
        if (outcome.Value is { } restored)
        {
            _release = restored;
            Toast.Success("ReleaseStatusRestored", restored.Version);
            return;
        }
        Toast.ErrorRaw(L["ReleaseStatusRestoreFailed"].Value, outcome.Error?.Message ?? string.Empty);
    }

    // The release status (Pending -> Deploying -> Deployed/Failed) flips while this page is open.
    // Subscribe to the project's release group so the badge updates live instead of needing a refresh.
    private async Task StartHubAsync(int projectId)
    {
        try
        {
            _hubConnection = HubFactory.Create("releases");
            _hubConnection.On<ReleaseDto>("ReleaseStatusChanged", OnReleaseChanged);
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinProjectGroup", projectId);
                await ReloadAsync();
            }));
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinProjectGroup", projectId);
        }
        catch
        {
            // SignalR is best-effort: fall back to the static load.
        }
    }

    internal Task OnReleaseChanged(ReleaseDto release)
    {
        if (release.Id != ReleaseId) return Task.CompletedTask;
        return InvokeAsync(async () =>
        {
            _release = release;
            ReassertBreadcrumb(release);
            await LoadLinkedCommitMessagesAsync(release);
            StateHasChanged();
        });
    }

    private async Task ReloadAsync()
    {
        try
        {
            _release = await Api.Projects.GetReleaseAsync(ReleaseId);
            if (_release is not null)
            {
                ReassertBreadcrumb(_release);
                await LoadLinkedCommitMessagesAsync(_release);
                _provenance = await LoadProvenanceAsync(ReleaseId) ?? _provenance;
            }
        }
        catch (HttpRequestException ex)
        {
            // Transient: keep the prior release, but leave a debug signal so a stuck status
            // (e.g. Deploying that never refreshes) is diagnosable instead of silently swallowed.
            Logger.LogWarning(ex, "Release {ReleaseId} reload failed; keeping the previously loaded snapshot.", ReleaseId);
        }
        StateHasChanged();
    }

    private void ReassertBreadcrumb(ReleaseDto release) => Breadcrumb.Set(
        new BreadcrumbItem(L["Projects"], "/projects"),
        new BreadcrumbItem(release.ProjectName, $"/projects/{release.ProjectId}/overview"),
        new BreadcrumbItem(L["Releases"], $"/projects/{release.ProjectId}/releases"),
        new BreadcrumbItem(ReleaseHelper.ShortVersion(release.Version)));

    public async ValueTask DisposeAsync()
    {
        _loadGeneration++;
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        await StopHubAsync();
    }

    private async Task StopHubAsync()
    {
        if (_hubConnection is not null)
        {
            if (_release is not null)
            {
                try { await _hubConnection.InvokeAsync("LeaveProjectGroup", _release.ProjectId); }
                catch { /* best-effort */ }
            }
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }
    }

    private sealed record ChangelogEntry(string? Type, string Text);

    private IReadOnlyList<ChangelogEntry> VisibleChangelogEntries
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_release?.Changelog))
                return ParseChangelogEntries(_release.Changelog);

            return _release?.Commits
                .Where(commit => !string.IsNullOrWhiteSpace(commit.Message))
                .Select(commit => commit.Message!)
                .Concat(_linkedCommitMessages)
                .Distinct(StringComparer.Ordinal)
                .SelectMany(ParseChangelogEntries)
                .ToList() ?? [];
        }
    }

    private async Task LoadLinkedCommitMessagesAsync(ReleaseDto release)
    {
        _linkedCommitMessages = [];
        if (!string.IsNullOrWhiteSpace(release.Changelog)) return;
        _linkedCommitMessages.AddRange(release.Commits
            .Select(commit => commit.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message!));

        var missingShas = release.Commits
            .Where(commit => string.IsNullOrWhiteSpace(commit.Message))
            .Select(commit => commit.Sha)
            .Where(sha => !string.IsNullOrWhiteSpace(sha))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
        if (missingShas.Count == 0) return;

        try
        {
            var repositories = await Api.Git.GetGitReposAsync(release.ProjectId);
            var repositoryId = GitRepositorySelection.Resolve(repositories, release.RepositoryUrl);
            if (repositoryId is null) return;

            var messages = await Api.Git.GetGitCommitMessagesAsync(repositoryId.Value, missingShas);
            foreach (var sha in missingShas)
                if (messages.TryGetValue(sha, out var message) && !string.IsNullOrWhiteSpace(message))
                    _linkedCommitMessages.Add(message);
        }
        catch (HttpRequestException)
        {
            // The release remains usable when historical Git metadata is unavailable.
        }
    }

    private static List<ChangelogEntry> ParseChangelogEntries(string changelog)
    {
        var entries = new List<ChangelogEntry>();
        foreach (var line in changelog.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart('-', '*', ' ');
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            string? type = null;
            var text = trimmed;
            if (trimmed.StartsWith("feat", StringComparison.OrdinalIgnoreCase))
                type = "feat";
            else if (trimmed.StartsWith("fix", StringComparison.OrdinalIgnoreCase))
                type = "fix";
            else if (trimmed.StartsWith("chore", StringComparison.OrdinalIgnoreCase))
                type = "chore";
            else if (trimmed.StartsWith("docs", StringComparison.OrdinalIgnoreCase))
                type = "docs";
            else if (trimmed.StartsWith("refactor", StringComparison.OrdinalIgnoreCase))
                type = "refactor";
            else if (trimmed.StartsWith("perf", StringComparison.OrdinalIgnoreCase))
                type = "perf";
            else if (trimmed.StartsWith("test", StringComparison.OrdinalIgnoreCase))
                type = "test";

            if (type is not null)
            {
                var colonIdx = trimmed.IndexOf(':', StringComparison.Ordinal);
                if (colonIdx > 0 && colonIdx < 20)
                    text = trimmed[(colonIdx + 1)..].TrimStart();
            }

            entries.Add(new ChangelogEntry(type, text));
        }

        if (entries.Count == 0)
            entries.Add(new ChangelogEntry(null, changelog.Trim()));

        return entries;
    }

    private static OmniTone GetChangelogBadge(string? type) => type switch
    {
        "feat" => OmniTone.Success,
        "fix" => OmniTone.Danger,
        "perf" => OmniTone.Warning,
        "docs" => OmniTone.Accent,
        _ => OmniTone.Neutral
    };
}
