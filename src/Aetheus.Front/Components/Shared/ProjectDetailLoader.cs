// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Microsoft.AspNetCore.Components.Routing;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Shared state for the project detail view - simplified version of <see cref="ServerDetailLoader"/>
/// without SignalR (projects don't have real-time heartbeats) or capability flags.
///
/// Scoped service (one per Blazor circuit). Section pages call <see cref="EnsureLoadedAsync"/>
/// with their route's <c>Id</c> parameter on <c>OnParametersSetAsync</c>; calls for the same id
/// no-op, calls for a different id swap the state cleanly.
///
/// Auto-clears on navigation away from <c>/projects/{id}/*</c>.
/// </summary>
public sealed class ProjectDetailLoader : IAsyncDisposable
{
    private readonly ApiClient _api;
    private readonly NavigationManager _nav;
    private readonly ILogger<ProjectDetailLoader> _logger;
    private readonly HubConnectionFactory _hubFactory;

    private int? _currentId;
    private CancellationTokenSource? _loadCts;
    private HubConnection? _releaseHub;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Set for the duration of TeardownAsync so a hub handler that fires mid-teardown
    // (ReleaseCreated/StatusChanged) bails instead of mutating a service being cleaned up.
    private volatile bool _disposing;

    public ProjectDetailDto? Project { get; private set; }
    public List<ReleaseDto> Releases { get; private set; } = [];
    public bool InitialLoadCompleted { get; private set; }

    /// <summary>Fired whenever <see cref="Project"/> or <see cref="Releases"/> change. UI re-renders.</summary>
    public event Action? OnChanged;

    public ProjectDetailLoader(ApiClient api, NavigationManager nav, ILogger<ProjectDetailLoader> logger, HubConnectionFactory hubFactory)
    {
        _api = api;
        _nav = nav;
        _logger = logger;
        _hubFactory = hubFactory;
        _nav.LocationChanged += OnLocationChanged;
    }

    /// <summary>
    /// Loads (or no-ops for the same id) the project detail and its releases. Safe to call from
    /// <c>OnParametersSetAsync</c> on every page render - the semaphore + id comparison protect
    /// against concurrent loads on the same id.
    /// </summary>
    public async Task EnsureLoadedAsync(int id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A completed load that found no project is final too (see ServerDetailLoader): the
            // "not found" state must not re-request on every render. ReloadAsync clears the id.
            if (_currentId == id && (Project is not null || InitialLoadCompleted)) return;

            await TeardownAsync().ConfigureAwait(false);

            _currentId = id;
            _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _loadCts.Token;

            var projectTask = _api.Projects.GetProjectDetailAsync(id, token);
            var releasesTask = _api.Projects.GetReleasesAsync(projectId: id);

            await Task.WhenAll(projectTask, releasesTask).ConfigureAwait(false);

            var project = await projectTask.ConfigureAwait(false);
            var releases = (await releasesTask.ConfigureAwait(false)).Items;
            if (token.IsCancellationRequested || _disposing || _currentId != id) return;
            Project = project;
            Releases = releases;
            InitialLoadCompleted = true;
            OnChanged?.Invoke();
            // Real-time enrichment is best effort and must not keep the initial project page
            // behind a loader while SignalR negotiates or retries.
            _ = StartReleaseHubAsync(id, token);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to load project {Id} - token may be expired", id);
            InitialLoadCompleted = true;
            OnChanged?.Invoke();
        }
        catch (OperationCanceledException) when (_currentId != id || _disposing)
        {
            // Navigation or a newer project load cancelled this request; stale state must stay discarded.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Force-reloads the project detail (clears the current id so <see cref="EnsureLoadedAsync"/>
    /// re-fetches). Used after edits to refresh stale data.
    /// </summary>
    public async Task ForceReloadAsync(int id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _currentId = null;
        }
        finally
        {
            _gate.Release();
        }

        await EnsureLoadedAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-syncs releases from the remote source and refreshes the local list.
    /// </summary>
    public async Task SyncReleasesAsync(int projectId, CancellationToken ct = default)
    {
        await _api.Projects.SyncReleasesAsync(projectId, ct).ConfigureAwait(false);
        var result = await _api.Projects.GetReleasesAsync(projectId: projectId).ConfigureAwait(false);
        Releases = result.Items;
        OnChanged?.Invoke();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var rel = _nav.ToBaseRelativePath(_nav.Uri);
        if (!rel.StartsWith("projects/", StringComparison.OrdinalIgnoreCase))
            _ = TeardownAsync();
    }

    /// <summary>
    /// Subscribes to the releases hub for this project so a release created by a pipeline
    /// (ReleaseCreated), a branch-scan detection (ReleasesUpdated) or a promote/rollback
    /// (ReleaseStatusChanged) refreshes the section without a manual reload. Best-effort:
    /// a hub failure silently degrades to static (the page still works via manual sync).
    /// </summary>
    private async Task StartReleaseHubAsync(int projectId, CancellationToken ct)
    {
        var hub = _hubFactory.Create("releases");
        // The reconnect handler reads hub, so ownership is tracked apart from the variable: nulling
        // hub to skip the dispose below left that handler throwing on every reconnection.
        var ownsHub = true;
        try
        {
            hub.On<ReleaseDto>("ReleaseCreated", _ => ReloadReleasesAsync(projectId));
            hub.On<ReleaseDto>("ReleaseStatusChanged", _ => ReloadReleasesAsync(projectId));
            hub.On<int, List<ReleaseDto>>("ReleasesUpdated", (pid, _) =>
                pid == projectId ? ReloadReleasesAsync(projectId) : Task.CompletedTask);
            // Group membership is per-connection and lost on auto-reconnect - re-join + reload.
            hub.RejoinOnReconnect(async () =>
            {
                await hub.InvokeAsync("JoinProjectGroup", projectId).ConfigureAwait(false);
                await ReloadReleasesAsync(projectId).ConfigureAwait(false);
            });
            await hub.StartAsync(ct).ConfigureAwait(false);
            await hub.InvokeAsync("JoinProjectGroup", projectId, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested || _disposing || _currentId != projectId) return;
            var previous = Interlocked.Exchange(ref _releaseHub, hub);
            if (previous is not null && !ReferenceEquals(previous, hub))
                await previous.DisposeAsync().ConfigureAwait(false);

            // Teardown may have raced the exchange. Remove and dispose this connection if the
            // project stopped being current between the two checks.
            if (ct.IsCancellationRequested || _disposing || _currentId != projectId)
                Interlocked.CompareExchange(ref _releaseHub, null, hub);
            else
                ownsHub = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Navigation cancelled a best-effort connection attempt.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Release hub unavailable for project {ProjectId}", projectId);
        }
        finally
        {
            if (ownsHub)
            {
                try { await hub.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Release hub dispose failed for project {ProjectId}", projectId); }
            }
        }
    }

    private async Task ReloadReleasesAsync(int projectId)
    {
        if (_disposing || _currentId != projectId) return;
        try
        {
            var result = await _api.Projects.GetReleasesAsync(projectId: projectId).ConfigureAwait(false);
            // Teardown may have raced the fetch - don't resurrect state on a cleaned-up loader.
            if (_disposing || _currentId != projectId) return;
            Releases = result.Items;
            OnChanged?.Invoke();
        }
        catch (HttpRequestException) { /* hub-triggered reload - silent on auth failure */ }
    }

    private async Task TeardownAsync()
    {
        _disposing = true;
        // Gate in-flight hub handlers immediately: a ReloadReleasesAsync already past its first
        // guard re-checks _currentId after its await, so nulling it here stops it writing back.
        _currentId = null;

        try { _loadCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed */ }
        _loadCts?.Dispose();
        _loadCts = null;
        var releaseHub = Interlocked.Exchange(ref _releaseHub, null);
        if (releaseHub is not null)
        {
            try { await releaseHub.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }

        Project = null;
        Releases = [];
        InitialLoadCompleted = false;
        OnChanged?.Invoke();
        _disposing = false;
    }

    public async ValueTask DisposeAsync()
    {
        _nav.LocationChanged -= OnLocationChanged;
        await TeardownAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
