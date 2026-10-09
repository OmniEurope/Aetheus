// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;

namespace Aetheus.Front.Components.Servers.AgentWizard;

/// <summary>
/// Owns the Verify-step real-time agent-detection lifecycle for <see cref="AddAgent"/>: a SignalR
/// subscription on the <c>servers</c> hub, a polling fallback, the wizard's own registration-token
/// fast-path, and the countdown/timeout timer. Extracted from <c>AddAgent</c> so the page stays within
/// the 600-line budget (FileSizeAuditTests) and detection is a single-responsibility collaborator.
/// The owning component supplies <paramref name="onChanged"/> (which marshals to the renderer via
/// InvokeAsync + StateHasChanged); this class never touches Blazor rendering directly.
/// </summary>
internal sealed class AgentDetectionMonitor(
    ApiClient api,
    HubConnectionFactory hubFactory,
    ILogger logger,
    Func<Task> onChanged,
    TimeProvider? timeProvider = null)
{
    public const int MaxSeconds = 120;

    private HubConnection? _hubConnection;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private CancellationTokenSource? _monitorCts;
    private Task _pollingTask = Task.CompletedTask;
    private Task _countdownTask = Task.CompletedTask;
    private long? _listeningStartedTimestamp;
    private RegistrationTokenDto? _generatedToken;
    private int _completionState;
    private int _tokenCheckInFlight;

    public bool Listening { get; private set; }
    public bool AgentFound { get; private set; }
    public string? AgentName { get; private set; }
    public int? DetectedServerId { get; private set; }
    public string? HubError { get; private set; }
    public bool TimedOut { get; private set; }

    public int ElapsedSeconds => _listeningStartedTimestamp is null
        ? 0
        : (int)Math.Min(MaxSeconds,
            _timeProvider.GetElapsedTime(_listeningStartedTimestamp.Value).TotalSeconds);

    // --- Real-time agent detection (Verify step) ---
    // SignalR subscription on the `servers` hub plus a polling fallback; DisposeAsync teardown.
    public async Task StartAsync(RegistrationTokenDto? generatedToken)
    {
        if (Listening) return;
        await StopAsync().ConfigureAwait(false);
        _generatedToken = generatedToken;

        Listening = true;
        AgentFound = false;
        HubError = null;
        TimedOut = false;
        Volatile.Write(ref _completionState, 0);
        _listeningStartedTimestamp = _timeProvider.GetTimestamp();
        _monitorCts = new CancellationTokenSource();
        _countdownTask = RunCountdownAsync(_monitorCts.Token);
        await onChanged().ConfigureAwait(false);

        _hubConnection = hubFactory.Create("servers");

        // FRONT-015: a hub event only says that SOME server the user can see registered or is alive. On
        // a fleet that is already running, an old server's heartbeat must never conclude that the new
        // agent is installed, so every event is just a cue to re-read this wizard's own token: only its
        // IsUsed/UsedByServerId (the same exact signal as the polling path) reports the agent found.
        _hubConnection.On<ServerDto>("ServerRegistered", _ => CheckTokenOnServerEventAsync());
        _hubConnection.On<int, ServerHeartbeatDto>("ServerHeartbeat", (_, _) => CheckTokenOnServerEventAsync());

        // Re-join on reconnect so a ServerRegistered broadcast still reaches the verify step after
        // a transient drop (group membership is per-connection). Polling remains the backstop.
        _hubConnection.RejoinOnReconnect(() => _hubConnection.InvokeAsync("JoinAllServers"));

        try
        {
            await _hubConnection.StartAsync().ConfigureAwait(false);
            await _hubConnection.InvokeAsync("JoinAllServers").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HubError = ex.Message;
            await _hubConnection.DisposeAsync().ConfigureAwait(false);
            _hubConnection = null;
        }

        await StartPollingAsync(_monitorCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Reacts to a <c>servers</c> hub event by re-reading the wizard's token. One check at a time: a
    /// fleet's heartbeats arriving together trigger a single request, not one per server.
    /// </summary>
    private async Task CheckTokenOnServerEventAsync()
    {
        if (AgentFound || Interlocked.CompareExchange(ref _tokenCheckInFlight, 1, 0) != 0) return;
        try
        {
            await TryDetectViaUsedTokenAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _tokenCheckInFlight, 0);
        }
    }

    private async Task MarkFoundAsync(string? name, int? serverId)
    {
        if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0) return;
        AgentFound = true;
        AgentName = name;
        DetectedServerId = serverId;
        Listening = false;
        _monitorCts?.Cancel();
        await onChanged().ConfigureAwait(false);
    }

    private async Task StartPollingAsync(CancellationToken ct)
    {
        // Fast path + sole poll signal: the wizard's own registration token flips to IsUsed the instant
        // the agent completes the handshake - exact and time-independent, so an agent that registered
        // before the user reached this step is caught immediately. DTC7: the old server-count and
        // 3-minute-heartbeat-window heuristics were removed - they could false-positive on a server
        // someone else registered concurrently (count grows / "newest" is fresh but isn't *our* agent).
        // The token path keys on UsedByServerId, so it only ever fires for this wizard's own agent.
        if (await TryDetectViaUsedTokenAsync().ConfigureAwait(false)) return;

        var timer = new PeriodicTimer(TimeSpan.FromSeconds(3), _timeProvider);
        _pollingTask = PollAsync(timer, ct);
    }

    private async Task PollAsync(PeriodicTimer timer, CancellationToken ct)
    {
        using (timer)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false) && !AgentFound)
                {
                    // Authoritative signal: our token being used means *this* agent registered.
                    if (await TryDetectViaUsedTokenAsync().ConfigureAwait(false)) break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // Silent - SignalR is the primary detection mechanism
                logger.LogWarning(ex, "[AddAgent] Polling fallback failed");
            }
        }
    }

    /// <summary>
    /// Deterministic detection of the wizard's own agent: its registration token flips to
    /// <see cref="RegistrationTokenDto.IsUsed"/> (with <see cref="RegistrationTokenDto.UsedByServerId"/>
    /// set) the moment the handshake completes. Unlike the count/heartbeat heuristics this is exact and
    /// time-independent, so an already-registered agent is reported instantly instead of leaving the
    /// verify step stuck on "Looking for new agents...".
    /// </summary>
    private async Task<bool> TryDetectViaUsedTokenAsync()
    {
        if (AgentFound || _generatedToken is null) return false;

        RegistrationTokenDto? token;
        try
        {
            // RTOK: poll only *our* token by id instead of downloading the full unpaginated list every tick.
            token = await api.Auth.GetRegistrationTokenAsync(_generatedToken.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AddAgent] Registration-token status check failed");
            return false;
        }

        if (token is { IsUsed: true, UsedByServerId: { } serverId })
        {
            await MarkFoundAsync(
                await ResolveServerNameAsync(serverId).ConfigureAwait(false), serverId).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<string?> ResolveServerNameAsync(int serverId)
    {
        try
        {
            var detail = await api.Servers.GetServerDetailAsync(serverId).ConfigureAwait(false);
            return detail?.Name ?? $"Server #{serverId}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AddAgent] Resolving detected server name failed");
            return $"Server #{serverId}";
        }
    }

    /// <summary>
    /// User-initiated bail-out from the Verify step: tears down SignalR + polling, clears any error
    /// and returns the UI to the "Check Connection" idle state.
    /// </summary>
    public async Task CancelAsync()
    {
        await StopAsync().ConfigureAwait(false);
        HubError = null;
        AgentFound = false;
        await onChanged().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        var cts = _monitorCts;
        _monitorCts = null;
        cts?.Cancel();
        await AwaitCancellationAsync(_pollingTask).ConfigureAwait(false);
        await AwaitCancellationAsync(_countdownTask).ConfigureAwait(false);
        cts?.Dispose();
        if (_hubConnection is null)
        {
            Listening = false;
            return;
        }

        try
        {
            await _hubConnection.InvokeAsync("LeaveAllServers").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Hub may already be disconnected
            logger.LogWarning(ex, "[AddAgent] LeaveAllServers failed");
        }

        await _hubConnection.DisposeAsync().ConfigureAwait(false);
        _hubConnection = null;
        Listening = false;
    }

    private async Task RunCountdownAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (AgentFound || !Listening) return;
                if (ElapsedSeconds >= MaxSeconds)
                {
                    if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0) return;
                    TimedOut = true;
                    Listening = false;
                    _monitorCts?.Cancel();
                    await DisposeHubAsync().ConfigureAwait(false);
                    await onChanged().ConfigureAwait(false);
                    return;
                }
                await onChanged().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task DisposeHubAsync()
    {
        if (_hubConnection is null) return;
        await _hubConnection.DisposeAsync().ConfigureAwait(false);
        _hubConnection = null;
    }

    private static async Task AwaitCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
