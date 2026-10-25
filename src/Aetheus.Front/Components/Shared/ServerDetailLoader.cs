// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Microsoft.AspNetCore.Components.Routing;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Item #1 of the plan - scaffolding for the URL refactor that replaces <c>?section=</c> with
/// real routes (<c>/servers/{id}/overview</c>, <c>/servers/{id}/services</c>, …).
///
/// The 21 future section pages all share a single ServerDetailLayout, and that layout owns:
///   - the one-shot <c>GetServerDetailAsync(id)</c> fetch
///   - the SignalR <c>server-{id}</c> group join / leave
///   - the capability flags (HasDocker, HasApache, …)
///   - the breadcrumb / nav-context broadcasts
///
/// This loader is the single source of truth for that shared state. Section pages call
/// <see cref="EnsureLoadedAsync"/> with their route's <c>Id</c> parameter on
/// <c>OnParametersSetAsync</c>; calls for the same id no-op, calls for a different id swap
/// the underlying connection + state cleanly.
///
/// Scoped service (one per Blazor circuit). Auto-clears on navigation away from
/// <c>/servers/{id}/*</c> - same pattern as the existing <see cref="ServerNavContextService"/>,
/// which this loader will subsume once the refactor lands fully.
/// </summary>
public sealed class ServerDetailLoader : IAsyncDisposable
{
    private readonly ApiClient _api;
    private readonly HubConnectionFactory _hubFactory;
    private readonly NavigationManager _nav;
    private readonly ILogger<ServerDetailLoader> _logger;
    private readonly PageStateCache? _pageState;

    private int? _currentId;
    private HubConnection? _hub;
    private CancellationTokenSource? _loadCts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ServerDetailDto? Server { get; private set; }
    public List<ServerMetricDto> Metrics { get; private set; } = [];
    public DateTime? LastUpdated { get; private set; }
    public bool MetricsReceived { get; private set; }
    public bool InitialLoadCompleted { get; private set; }

    /// <summary>TC8R: latest agent-reported pipeline-toolchain capability (.NET SDK + git), from the
    /// heartbeat. Null until the first heartbeat arrives or for a pre-feature agent (unknown).</summary>
    public bool? PipelineRunnerAvailable { get; private set; }

    /// <summary>
    /// The shared "servers" SignalR connection. Exposed so the layout chrome can hand it to
    /// <c>AgentUpdateProgressCard</c> (which subscribes to the agent self-update events on the
    /// same connection - no double-subscribe on the server group). Null until the load completes.
    /// </summary>
    public HubConnection? Hub => _hub;

    /// <summary>Fired whenever <see cref="Server"/> / metric flags change. UI re-renders.</summary>
    public event Action? OnChanged;

    /// <summary>Notify section subscribers when a task for the loaded server completes so each
    /// section refreshes its own affected state.</summary>
    public event Func<TaskCompletedNotification, Task>? OnTaskCompleted;

    public ServerDetailLoader(ApiClient api, HubConnectionFactory hubFactory, NavigationManager nav, ILogger<ServerDetailLoader> logger,
        PageStateCache? pageState = null)
    {
        _api = api;
        _hubFactory = hubFactory;
        _nav = nav;
        _logger = logger;
        _pageState = pageState;
        _nav.LocationChanged += OnLocationChanged;
    }

    /// <summary>
    /// Loads (or no-ops for the same id) the server detail and joins its SignalR group. Safe
    /// to call from <c>OnParametersSetAsync</c> on every page render - the semaphore + id
    /// comparison protect against concurrent loads on the same id.
    /// </summary>
    public async Task EnsureLoadedAsync(int id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A completed load that found no server is final too: re-requesting on every render of the
            // "not found" state looped until the API answered 429.
            if (_currentId == id && (Server is not null || InitialLoadCompleted)) return;
            await LoadAsync(id, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // A server that does not exist (404) or cannot be read: the layout shows its "server not
            // found" state instead of the error boundary, as the project loader already does.
            _logger.LogWarning(ex, "[ServerDetailLoader] Failed to load server {ServerId}", id);
            Server = null;
            _pageState?.Forget<ServerDetailState>($"servers/{id}");
            InitialLoadCompleted = true;
            OnChanged?.Invoke();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The reader left the server pages while this load was in flight: the leave cancelled it.
            // Nothing is loaded, and the next server page asks again.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadAsync(int id, CancellationToken ct)
    {
        // Switching servers: empty the loader and cancel any in-flight load so an out-of-order response
        // cannot overwrite the new server's data. The old connection leaves its group on its own: the
        // new server's first display never waits for a hub that is slow to answer (R-16).
        _ = TeardownAsync();

        _currentId = id;
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _loadCts.Token;

        // Show the cached server detail immediately on return, then replace it with the fetched
        // state so navigation remains responsive.
        var address = $"servers/{id}";
        var ticket = _pageState?.Begin() ?? default;
        ShowRememberedState(address);

        Server = await _api.Servers.GetServerDetailAsync(id, token).ConfigureAwait(false);
        Metrics = await LoadMetricsAsync(id, afterUtc: null, ct: token).ConfigureAwait(false);
        InitialLoadCompleted = true;
        RememberLoadedState(address, ticket);
        if (Server is { MemoryTotalMb: > 0 })
        {
            MetricsReceived = true;
            LastUpdated = DateTime.Now;
        }
        OnChanged?.Invoke();

        if (Server is not null)
        {
            // Heartbeats enrich the page after its initial HTTP state is visible. SignalR
            // negotiation is best effort and must never hold every server section loader.
            _ = StartHubAsync(id, token);
        }
    }

    private void ShowRememberedState(string address)
    {
        if (_pageState?.TryRestore<ServerDetailState>(address, out var shown) != true) return;
        Server = shown.Server;
        Metrics = [.. shown.Metrics];
        OnChanged?.Invoke();
    }

    private void RememberLoadedState(string address, ListCacheService.Ticket ticket)
    {
        if (Server is null) _pageState?.Forget<ServerDetailState>(address);
        else _pageState?.Remember(ticket, address, new ServerDetailState(Server, [.. Metrics]));
    }

    /// <summary>
    /// Forces a re-fetch of the loaded server's detail (keeping the SignalR group intact) so a
    /// state-changing operation - service install/uninstall/lifecycle - reflects immediately instead
    /// of waiting for the next heartbeat. No-ops when nothing is loaded or the id no longer matches.
    /// </summary>
    public async Task RefreshAsync(int id, CancellationToken ct = default)
    {
        if (_currentId != id || Server is null) return;

        var heartbeatsBefore = Volatile.Read(ref _heartbeatsApplied);
        var fresh = await _api.Servers.GetServerDetailAsync(id, ct).ConfigureAwait(false);
        if (fresh is null || _currentId != id || Server is null) return;

        Server = KeepNewerServices(Server, fresh, Volatile.Read(ref _heartbeatsApplied) != heartbeatsBefore);
        DateTime? afterUtc = Metrics.Count == 0 ? null : Metrics[^1].Timestamp;
        var newMetrics = await LoadMetricsAsync(id, afterUtc, ct).ConfigureAwait(false);
        if (newMetrics.Count > 0)
            Metrics.AddRange(newMetrics);
        TrimMetricWindow(DateTime.Now);
        LastUpdated = DateTime.Now;
        OnChanged?.Invoke();
    }

    // Do not overwrite a newer live heartbeat with a stale post-action read; retain unchanged
    // service-list instances so the page does not mistake them for new action results.
    private int _heartbeatsApplied;

    private void ReloadAgentUpdate(int serverId, int id, CancellationToken ct)
    {
        if (serverId == id && !IsStale(id, ct)) _ = ReloadAgentUpdateAsync(id, ct);
    }

    // The connection of a server the reader left keeps delivering events until its background teardown
    // closes it (R-16): such an event belongs to a load that was cancelled, or to another server now.
    private bool IsStale(int id, CancellationToken ct) => ct.IsCancellationRequested || _currentId != id;

    /// <summary>Refresh agent version, compatibility and persisted update request without
    /// overwriting live heartbeat fields; a failed read retains the displayed state for the next
    /// event to refresh.</summary>
    internal async Task ReloadAgentUpdateAsync(int id, CancellationToken ct)
    {
        try
        {
            var fresh = await _api.Servers.GetServerDetailAsync(id, ct).ConfigureAwait(false);
            if (fresh is null || Server is null || _currentId != id) return;
            Server = Server with
            {
                AgentVersion = fresh.AgentVersion,
                AgentCompatibility = fresh.AgentCompatibility,
                AgentUpdateRequest = fresh.AgentUpdateRequest
            };
            OnChanged?.Invoke();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[ServerDetailLoader] Agent update state reload failed for server {ServerId}", id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The page left this server.
        }
    }

    /// <summary>Re-reads the external probe result recorded with the Offline transition, without
    /// touching live heartbeat fields; a failed read keeps the displayed state.</summary>
    internal async Task ReloadExternalProbeAsync(int id, CancellationToken ct)
    {
        try
        {
            var fresh = await _api.Servers.GetServerDetailAsync(id, ct).ConfigureAwait(false);
            if (fresh is null || Server is null || _currentId != id) return;
            Server = Server with
            {
                ExternalProbe = fresh.ExternalProbe,
                ExternalProbeAt = fresh.ExternalProbeAt,
                ExternalProbeTarget = fresh.ExternalProbeTarget
            };
            OnChanged?.Invoke();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[ServerDetailLoader] External probe reload failed for server {ServerId}", id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The page left this server.
        }
    }

    /// <summary>
    /// The reconnect work of the server group: re-join, then re-read the persisted agent update, even
    /// when the re-join failed (the read is plain HTTP and still brings the verdict).
    /// </summary>
    internal async Task RejoinAndReloadAgentUpdateAsync(Func<Task> rejoin, int id, CancellationToken ct)
    {
        try
        {
            await rejoin().ConfigureAwait(false);
        }
        finally
        {
            await ReloadAgentUpdateAsync(id, ct).ConfigureAwait(false);
        }
    }

    internal static ServerDetailDto KeepNewerServices(ServerDetailDto current, ServerDetailDto fetched, bool heartbeatDuringFetch) =>
        heartbeatDuringFetch || current.Services.SequenceEqual(fetched.Services)
            ? fetched with { Services = current.Services }
            : fetched;

    private async Task StartHubAsync(int id, CancellationToken ct)
    {
        var hub = _hubFactory.Create("servers");
        // The reconnect handler reads hub, so ownership is tracked apart from the variable: nulling
        // hub to skip the dispose below left that handler throwing on every reconnection.
        var ownsHub = true;

        hub.On<ServerHeartbeatDto>("Heartbeat", h =>
        {
            // A connection being closed (the load it belongs to was cancelled) must not feed the next server.
            if (Server is null || IsStale(id, ct)) return;
            var capabilities = ResolveSudoersCapabilities(Server, h);
            var clock = h.ClockSample?.Estimate();
            Server = Server with
            {
                CpuPercent = h.CpuPercent,
                ClockOffsetMs = clock?.OffsetMs,
                ClockOffsetUncertaintyMs = clock?.UncertaintyMs,
                MemoryUsedMb = h.MemoryUsedMb,
                MemoryTotalMb = h.MemoryTotalMb,
                DiskUsedGb = DiskInfoDto.Totals(h.Disks).UsedGb,
                DiskTotalGb = DiskInfoDto.Totals(h.Disks).TotalGb,
                InodeUsedPercent = DiskInfoDto.MaxInodeUsedPercent(h.Disks),
                Disks = DiskInfoDto.Distinct(h.Disks),
                Services = h.Services,
                Docker = h.Docker,
                Apache = h.Apache,
                Certbot = h.Certbot,
                Cron = h.Cron,
                Mail = h.Mail,
                Teamspeak = h.Teamspeak,
                Portsentry = h.Portsentry,
                Rkhunter = h.Rkhunter,
                AgentVersion = h.AgentVersion ?? Server.AgentVersion,
                AgentInstalledAt = h.AgentInstalledAt ?? Server.AgentInstalledAt,
                // Mirror the backend's per-heartbeat capability self-heal (ServerHeartbeatProcessor):
                // these flags are derived from the agent-reported sudoers drop-ins / docker probe and
                // self-heal both ways every beat. Without copying them onto the live record they stayed
                // frozen at the initial-load value, so a "package management not enabled" warning lingered
                // until a full reload even after a re-install with --enable-package-manage flipped it on.
                DockerAvailable = h.DockerAvailable,
                PackageManagementAvailable = capabilities.PackageManagement,
                MailSetupAvailable = capabilities.MailSetup,
                DeploymentTargetAvailable = capabilities.DeploymentTarget,
                InsecureTls = h.InsecureTls,
                // Null on a beat without the measure, as the backend clears it then: unknown.
                OomKillsSinceBoot = h.OomKills?.KillCount,
                StorageDiagnostics = h.StorageDiagnostics,
                // S-TECH-43: stamp the live heartbeat so "last seen" refreshes in real-time instead
                // of staying frozen at the value captured on the initial load (browser-local clock).
                LastHeartbeat = DateTime.Now,
                Status = ServerStatus.Online
            };
            Interlocked.Increment(ref _heartbeatsApplied);
            PipelineRunnerAvailable = h.PipelineRunnerAvailable;
            AppendMetric(h);
            MetricsReceived = true;
            LastUpdated = DateTime.Now;
            OnChanged?.Invoke();
        });

        hub.On<int>("ServerOffline", serverId =>
        {
            if (Server is null || serverId != id || IsStale(id, ct)) return;
            Server = Server with { Status = ServerStatus.Offline };
            OnChanged?.Invoke();
            // The backend announces the transition after its external probe (PLAN-010 lot 1).
            _ = ReloadExternalProbeAsync(id, ct);
        });

        hub.On<TaskCompletedNotification>("TaskCompleted", async notification =>
        {
            if (notification.ServerId != id || IsStale(id, ct)) return;
            var handlers = OnTaskCompleted?.GetInvocationList()
                .Cast<Func<TaskCompletedNotification, Task>>()
                .Select(handler => handler(notification))
                .ToArray() ?? [];
            await Task.WhenAll(handlers).ConfigureAwait(false);
        });

        // Reload update request and agent version on each agent-update event so the overview
        // reflects completion or the failure reason.
        hub.On<int>("AgentUpdateQueued", serverId => ReloadAgentUpdate(serverId, id, ct));
        hub.On<AgentUpdateProgressDto>("AgentUpdateProgress", progress =>
        {
            if (progress.Phase is AgentUpdatePhase.Failed or AgentUpdatePhase.Done)
                ReloadAgentUpdate(progress.ServerId, id, ct);
        });
        hub.On<int, int>("AgentUpdateConfirmed", (serverId, _) => ReloadAgentUpdate(serverId, id, ct));
        hub.On<int, int>("AgentUpdateFailed", (serverId, _) => ReloadAgentUpdate(serverId, id, ct));

        // Rejoin the server group after reconnect because membership is connection-scoped; reread
        // update verdicts during catch-up because SignalR does not replay missed events.
        hub.RejoinOnReconnect(() => RejoinAndReloadAgentUpdateAsync(() => hub.InvokeAsync("JoinServerGroup", id), id, ct));
        try
        {
            await hub.StartAsync(ct).ConfigureAwait(false);
            await hub.InvokeAsync("JoinServerGroup", id, ct).ConfigureAwait(false);

            if (await AdoptHubAsync(hub, id, ct).ConfigureAwait(false))
            {
                ownsHub = false;
                OnChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Navigation cancelled a best-effort connection attempt.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ServerDetailLoader] Real-time connection unavailable for server {ServerId}",
                id);
        }
        finally
        {
            if (ownsHub)
                await DisposeQuietlyAsync(hub).ConfigureAwait(false);
        }
    }

    /// <summary>Makes a started connection the loader's own, replacing (and disposing) the previous
    /// one, unless the server is no longer current. Teardown may race the exchange: a connection
    /// for a server that is no longer current is released again. True when the loader kept it.</summary>
    private async Task<bool> AdoptHubAsync(HubConnection hub, int id, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _currentId != id) return false;
        var previous = Interlocked.Exchange(ref _hub, hub);
        if (previous is not null && !ReferenceEquals(previous, hub))
            await previous.DisposeAsync().ConfigureAwait(false);

        if (!ct.IsCancellationRequested && _currentId == id) return true;
        Interlocked.CompareExchange(ref _hub, null, hub);
        return false;
    }

    private async Task DisposeQuietlyAsync(HubConnection hub)
    {
        try { await hub.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "[ServerDetailLoader] hub dispose failed"); }
    }

    internal static (bool? PackageManagement, bool MailSetup, bool? DeploymentTarget)
        ResolveSudoersCapabilities(ServerDetailDto server, ServerHeartbeatDto heartbeat)
    {
        var authoritative = heartbeat.SudoersInventoryAvailable == true
            || heartbeat.SudoersHashes.Count > 0;
        return authoritative
            ? (heartbeat.SudoersHashes.ContainsKey("aetheus-package"),
                heartbeat.SudoersHashes.ContainsKey("aetheus-mail"),
                heartbeat.SudoersHashes.ContainsKey("aetheus-deploy"))
            : (server.PackageManagementAvailable, server.MailSetupAvailable, server.DeploymentTargetAvailable);
    }

    /// <summary>Capability flags computed from the loaded server - sane defaults when not loaded.</summary>
    public bool HasDocker => Server is { Type: ServerType.Docker } ||
                             Server?.Docker is { Containers.Count: > 0 } ||
                             Server?.Docker is { Images.Count: > 0 } ||
                             Server?.Docker is { ComposeStacks.Count: > 0 } ||
                             HasService("docker");
    public bool HasApache => Server?.Apache.IsInstalled == true || HasService("apache2");
    public bool HasCertbot => Server?.Certbot.IsInstalled == true || HasService("certbot");
    public bool HasCron => Server?.Cron.IsInstalled == true || HasService("cron") || HasService("crond");
    public bool HasMail => Server?.Mail.IsInstalled == true || HasService("postfix") || HasService("dovecot");
    public bool HasTeamspeak => Server?.Teamspeak.IsInstalled == true;
    public bool HasPortsentry => Server?.Portsentry.IsInstalled == true || HasService("portsentry");
    public bool HasRkhunter => Server?.Rkhunter.IsInstalled == true || HasService("rkhunter");

    private bool HasService(string name) =>
        Server?.Services.Any(s => s.IsInstalled && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;

    private async Task<List<ServerMetricDto>> LoadMetricsAsync(
        int id,
        DateTime? afterUtc,
        CancellationToken ct)
    {
        try
        {
            return await _api.Monitoring.GetServerMetricsAsync(
                id,
                24,
                ct,
                afterUtc,
                take: 1_000).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "[ServerDetailLoader] Storage metric history could not be loaded for server {ServerId}", id);
            return [];
        }
    }

    private void AppendMetric(ServerHeartbeatDto heartbeat)
    {
        var storage = heartbeat.StorageDiagnostics;
        var now = storage.CollectedAtUtc.Year < 2000
            ? DateTime.Now
            : ToLocalClock(storage.CollectedAtUtc);
        Metrics.Add(ServerMetricDto.FromHeartbeat(heartbeat, Server?.Id ?? 0, now));

        TrimMetricWindow(now);
    }

    internal static DateTime ToLocalClock(DateTime timestamp, TimeZoneInfo? timeZone = null)
    {
        if (timestamp.Kind == DateTimeKind.Local && timeZone is null)
            return timestamp;

        var utc = timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone ?? TimeZoneInfo.Local);
    }

    private void TrimMetricWindow(DateTime localNow)
    {
        var cutoff = localNow.AddHours(-24);
        Metrics.RemoveAll(metric => metric.Timestamp < cutoff);
        if (Metrics.Count > 1_000)
            Metrics.RemoveRange(0, Metrics.Count - 1_000);
    }

    /// <summary>
    /// Applies an edit (name / type / tags) from the layout chrome's inline edit panel onto the
    /// loaded <see cref="Server"/> and fires <see cref="OnChanged"/> so the header re-renders.
    /// No-ops when nothing is loaded.
    /// </summary>
    public void UpdateServerFields(string name, ServerType type, IReadOnlyList<string> tags)
    {
        if (Server is null) return;
        Server = Server with { Name = name, Type = type, Tags = [.. tags] };
        OnChanged?.Invoke();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        // Auto-clear when the user navigates away from /servers/{id}/* - symmetric with the
        // existing ServerNavContextService and avoids leaking the SignalR connection across
        // unrelated pages.
        if (OnServerPages) return;
        // A load still in flight is for a page the reader has left: stop it now, without waiting.
        try { _loadCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed */ }
        _ = TeardownOnLeaveAsync();
    }

    private bool OnServerPages =>
        _nav.ToBaseRelativePath(_nav.Uri).StartsWith("servers/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Serialize teardown behind in-flight loading and discard it if a server page is
    /// active again so late cleanup cannot empty a newly reused loader.</summary>
    internal async Task TeardownOnLeaveAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // the loader itself is gone
        }

        // The gate is held for the state only: the connection closes after it is released, so a reader
        // coming back to the server never waits behind the hub's answer (R-16).
        Task closing = Task.CompletedTask;
        try
        {
            if (!OnServerPages)
                closing = TeardownAsync();
        }
        finally
        {
            _gate.Release();
        }

        await closing.ConfigureAwait(false);
    }

    /// <summary>Cancels the load and empties the loader at once (the cancelled token also silences the
    /// old connection's heartbeats). The returned task is the old connection leaving its group and
    /// closing; it never faults, and only a disposing caller waits for it.</summary>
    private Task TeardownAsync()
    {
        try { _loadCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed */ }
        _loadCts?.Dispose();
        _loadCts = null;
        var hub = Interlocked.Exchange(ref _hub, null);
        var leftId = _currentId;

        _currentId = null;
        Server = null;
        Metrics = [];
        LastUpdated = null;
        MetricsReceived = false;
        InitialLoadCompleted = false;
        OnChanged?.Invoke();

        return hub is null ? Task.CompletedTask : CloseHubAsync(hub, leftId);
    }

    private async Task CloseHubAsync(HubConnection hub, int? serverId)
    {
        try
        {
            if (serverId.HasValue)
                await hub.InvokeAsync("LeaveServerGroup", serverId.Value).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[ServerDetailLoader] LeaveServerGroup failed"); }
        await DisposeQuietlyAsync(hub).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _nav.LocationChanged -= OnLocationChanged;
        await TeardownAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>The server detail last shown, kept for a return to its pages.</summary>
    private sealed record ServerDetailState(ServerDetailDto Server, List<ServerMetricDto> Metrics);
}
