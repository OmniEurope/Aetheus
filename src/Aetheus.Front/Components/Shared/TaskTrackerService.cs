// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Item #7 of the plan: maintains a live list of in-flight tasks (Pending / Assigned / Running)
/// visible to the current user. Drives the top-bar task tracker widget.
///
/// Single source of truth in the front for "what's running right now". Backend pushes:
///   - <c>TaskQueued</c>    : new task created - added to the list.
///   - <c>TaskStarted</c>   : task picked up by an agent - status flips to Running.
///   - <c>TaskCompleted</c> : task done (Success/Failed/Cancelled) - removed from the list.
///
/// Hub group scoping is delegated to <see cref="ServerHub.JoinAllServers"/> on the server,
/// which only adds the connection to per-server groups the user can <c>Read</c>. So this
/// service inherits RBAC for free - no event arrives for an inaccessible server.
///
/// Lifecycle: scoped (one per Blazor circuit/WASM session). Started on user login, stopped on
/// logout. Auto-refetches <c>/api/tasks/active</c> on every (re)connect so a network blip
/// cannot leave the widget out of sync.
/// </summary>
public sealed class TaskTrackerService : IAsyncDisposable
{
    private readonly ApiClient _api;
    private readonly AuthStateProvider _auth;
    private readonly HubConnectionFactory _hubFactory;
    private readonly ILogger<TaskTrackerService> _logger;

    private HubConnection? _hub;
    private readonly object _gate = new();
    private readonly Dictionary<int, ServerTaskDto> _byId = [];

    // Drives the initial-connect retry loop (see ScheduleInitialRetry) - cancelled whenever the hub
    // is torn down (StopAsync) so a superseded retry attempt never races a fresh one.
    private CancellationTokenSource? _startRetryCts;

    /// <summary>Fired whenever the visible set changes. UI subscribers re-render.</summary>
    public event Action? OnChanged;
    public event Action<TaskCompletedNotification>? OnTaskCompleted;

    /// <summary>Current SignalR connection state for the global status indicator.</summary>
    public HubConnectionState ConnectionState => _hub?.State ?? HubConnectionState.Disconnected;
    internal bool IsInitialized => _hub is not null;

    /// <summary>Fired on connection state transitions (connected/reconnecting/disconnected).</summary>
    public event Action? OnConnectionStateChanged;

    /// <summary>
    /// Fired each time the automatic-reconnect policy schedules a retry, carrying the delay (seconds)
    /// before that attempt. Drives the connection-lost dialog countdown. Mirrors the Astraia wiring.
    /// </summary>
    public event Action<int>? OnReconnectAttempt;

    // The shared schedule is surfaced to the UI countdown before returning each delay to the transport.
    private static IReadOnlyList<TimeSpan> RetryDelays => FrontendRuntimeDefaults.SignalRRetryDelays;

    /// <summary>Expose the last connection failure reason so the connection-lost dialog can explain
    /// the problem; clear it after a successful connection.</summary>
    public string? LastFailureReason { get; private set; }

    /// <summary>How long one connection attempt may wait for the server before it counts as failed.</summary>
    internal TimeSpan ConnectAttemptTimeout { get; init; } = FrontendRuntimeDefaults.SignalRStartTimeout;

    /// <summary>The clock the attempt deadline and the retry backoff run on.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    // Allow immediate reconnection to interrupt the bounded backoff so restored network access does
    // not wait for the remaining delay.
    private TaskCompletionSource _wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pendingWake;

    /// <summary>
    /// Interrupts the current backoff sleep so the retry loop attempts immediately. Called when the
    /// browser reports the network is back (<c>online</c>) or the tab becomes visible again after a
    /// sleep, which is the only moment the app can know the wait has become pointless. Safe to call
    /// when nothing is retrying: the signal is simply consumed by the next wait.
    /// </summary>
    public void RequestImmediateReconnect()
    {
        Interlocked.Exchange(ref _pendingWake, 1);
        Volatile.Read(ref _wakeSignal).TrySetResult();
    }

    // An online event can arrive while a failed connect is finishing, before the next wait is armed.
    // Carry that event into the next wait instead of sleeping through a recovered network.
    private Task ArmWake()
    {
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _wakeSignal, fresh);
        if (Interlocked.Exchange(ref _pendingWake, 0) != 0)
            fresh.TrySetResult();
        return fresh.Task;
    }

    public TaskTrackerService(ApiClient api, AuthStateProvider auth, HubConnectionFactory hubFactory, ILogger<TaskTrackerService> logger)
    {
        _api = api;
        _auth = auth;
        _hubFactory = hubFactory;
        _logger = logger;
    }

    /// <summary>Snapshot of the currently-tracked tasks (newest first).</summary>
    public IReadOnlyList<ServerTaskDto> Tasks
    {
        get
        {
            lock (_gate)
                return _byId.Values.OrderByDescending(t => t.CreatedAt).ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate) return _byId.Count;
        }
    }

    /// <summary>
    /// Connects the hub and seeds the list from the active-tasks endpoint. Idempotent - calling
    /// twice (e.g. on login then on first widget render) is a no-op after the first call.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_hub is not null) return;
        if (!_auth.IsAuthenticated) return;

        _hub = _hubFactory.Create("servers", new NotifyingRetryPolicy(this));
        _hub.On<ServerTaskDto>("TaskQueued", task => Upsert(task));
        _hub.On<TaskStartedEvent>("TaskStarted", evt => MarkRunning(evt.TaskId, evt.StartedAt));
        _hub.On<TaskCompletedNotification>("TaskCompleted", HandleTerminal);
        // Refresh active tasks on agent-update confirmation or failure because that lifecycle
        // completes through heartbeat or deadline events rather than TaskCompleted.
        _hub.On<int, int>("AgentUpdateConfirmed", (_, _) => SeedActiveAsync(CancellationToken.None));
        _hub.On<int, int>("AgentUpdateFailed", (_, _) => SeedActiveAsync(CancellationToken.None));

        // Refetch from /active after every (re)connect to recover any events missed while
        // disconnected. Same call seeds the initial set on first connect.
        _hub.Closed += _ =>
        {
            OnConnectionStateChanged?.Invoke();
            // Restart the initial connection loop when automatic reconnect gives up so recovery
            // continues; clear _hub before intentional disposal so teardown cannot resurrect the
            // connection.
            if (_hub is not null && _auth.IsAuthenticated) ScheduleInitialRetry();
            return Task.CompletedTask;
        };
        _hub.Reconnecting += _ => { OnConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
        _hub.RejoinOnReconnect(async () =>
        {
            OnConnectionStateChanged?.Invoke();
            await SeedActiveAsync(CancellationToken.None).ConfigureAwait(false);
            try { await _hub!.InvokeAsync("JoinAllServers", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "[TaskTracker] JoinAllServers on reconnect failed"); }
        });

        if (await ConnectOnceAsync(ct).ConfigureAwait(false))
            return;

        // The retry policy above only drives SignalR's *automatic* reconnect, which only arms after
        // a FIRST successful connect - a backend that is down at initial load would otherwise leave
        // the hub Disconnected forever with no retry, stranding real-time until a manual page reload.
        // Keep retrying the same (never-connected) hub on the same backoff schedule until it succeeds,
        // notifying OnReconnectAttempt so the connection-lost dialog's countdown can arm too.
        ScheduleInitialRetry();
    }

    private async Task<bool> ConnectOnceAsync(CancellationToken ct)
    {
        // The connection this attempt is for. The layout no longer awaits the first start, so a sign-out
        // or an account switch can replace _hub while it runs: what follows acts on this one only.
        var hub = _hub;
        if (hub is null) return false;
        try
        {
            // Bounded: a negotiate that never answers held the attempt forever, so the loop scheduled
            // nothing more, the countdown ran out and the overlay stayed with no reason to show.
            using (var deadline = new CancellationTokenSource(ConnectAttemptTimeout, Clock))
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token))
            {
                try
                {
                    await hub.StartAsync(attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"The realtime server did not answer within {ConnectAttemptTimeout.TotalSeconds:0} s.");
                }
            }
            LastFailureReason = null;
            OnConnectionStateChanged?.Invoke();
            await hub.InvokeAsync("JoinAllServers", ct).ConfigureAwait(false);
            await SeedActiveAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // A connection torn down meanwhile failed because it was disposed: nothing to report.
            if (!ReferenceEquals(hub, _hub)) return false;
            // The tracker is a UI affordance - don't fail the whole login if the hub is sour.
            _logger.LogWarning(ex, "[TaskTracker] StartAsync failed; widget will stay empty");
            // Surfaced by the connection-lost dialog. A message the user can read and quote is the
            // difference between a reproducible incident and "it stayed stuck, I pressed F5".
            LastFailureReason = ex.Message;
            OnConnectionStateChanged?.Invoke();
            return false;
        }
    }

    private void ScheduleInitialRetry()
    {
        _startRetryCts?.Cancel();
        _startRetryCts?.Dispose();
        _startRetryCts = new CancellationTokenSource();
        _ = InitialRetryLoopAsync(_startRetryCts.Token);
    }

    private async Task InitialRetryLoopAsync(CancellationToken token)
    {
        // This loop is the tracker's own reconnect (SignalR's gave up, or never had a first connect),
        // so it is a catch-up too: a page brought back while it runs shows the synchronising indicator
        // until the hub is connected and the task list seeded, or the loop is abandoned.
        using var catchUp = _hubFactory.SyncStatus.BeginCatchUp();
        var connected = await RetryUntilConnectedAsync(
            ConnectOnceAsync,
            RetryDelays,
            delay => OnReconnectAttempt?.Invoke(delay),
            () => _hub is not null && _auth.IsAuthenticated,
            token,
            ArmWake,
            Clock).ConfigureAwait(false);
        if (connected)
        {
            // Connected: retire the retry CTS instead of leaking it until the next Stop/Dispose.
            _startRetryCts?.Dispose();
            _startRetryCts = null;
        }
    }

    internal static async Task<bool> RetryUntilConnectedAsync(
        Func<CancellationToken, Task<bool>> connectAsync,
        IReadOnlyList<TimeSpan> delays,
        Action<int> onAttempt,
        Func<bool> canRetry,
        CancellationToken token,
        Func<Task>? armWake = null,
        TimeProvider? clock = null)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested && canRetry())
        {
            var delay = delays[Math.Min(attempt, delays.Count - 1)];
            onAttempt((int)delay.TotalSeconds);
            var woken = armWake?.Invoke();
            using (var sleepCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var sleeping = Task.Delay(delay, clock ?? TimeProvider.System, sleepCts.Token);
                if (woken is null)
                {
                    try { await sleeping.ConfigureAwait(false); }
                    catch (OperationCanceledException) { return false; }
                }
                else
                {
                    // Whichever comes first: the scheduled backoff, or the browser telling us the
                    // network is back. The attempt counter is deliberately not reset by a wake - a
                    // wake that does not actually fix connectivity must not restart the schedule
                    // from zero and hammer a backend that is genuinely down.
                    await Task.WhenAny(sleeping, woken).ConfigureAwait(false);
                    sleepCts.Cancel();
                }
            }
            if (token.IsCancellationRequested || !canRetry()) return false;
            if (await connectAsync(token).ConfigureAwait(false))
                return true;
            attempt++;
        }
        return false;
    }

    private async Task SeedActiveAsync(CancellationToken ct)
    {
        try
        {
            var active = await _api.Pipelines.GetActiveTasksAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                _byId.Clear();
                foreach (var t in active)
                    _byId[t.Id] = t;
            }
            OnChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TaskTracker] SeedActiveAsync failed");
        }
    }

    internal void Upsert(ServerTaskDto task)
    {
        lock (_gate)
            _byId[task.Id] = task;
        OnChanged?.Invoke();
    }

    internal void MarkRunning(int taskId, DateTime? startedAt)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(taskId, out var existing)) return;
            _byId[taskId] = existing with
            {
                Status = TaskExecutionStatus.Running,
                StartedAt = startedAt ?? existing.StartedAt
            };
        }
        OnChanged?.Invoke();
    }

    internal void HandleTerminal(TaskCompletedNotification notification)
    {
        bool removed;
        lock (_gate)
            removed = _byId.Remove(notification.TaskId);
        if (removed) OnChanged?.Invoke();
        OnTaskCompleted?.Invoke(notification);
    }

    /// <summary>
    /// Forces a fresh connection attempt - used by the connection-lost dialog's manual "reconnect"
    /// button. Tears down the (dead) hub and starts a new one; the state-change events fire through
    /// the normal path so the dialog closes on success or re-arms its countdown on failure.
    /// </summary>
    public async Task<ReconnectOutcome> ReconnectAsync(CancellationToken ct = default)
    {
        // Checked before tearing anything down. StartAsync returns immediately when the session is
        // gone, so the old path left the user with no hub AND no retry loop, and the button looked
        // like it had done nothing. Say so instead, and leave the existing connection alone.
        if (!_auth.IsAuthenticated)
        {
            LastFailureReason = null;
            return ReconnectOutcome.SessionExpired;
        }

        // Keep the last-known task list visible across the reconnect attempt instead of blanking the
        // widget immediately - ConnectOnceAsync's SeedActiveAsync repopulates it once the new hub
        // connects; if the attempt fails, a stale-but-non-empty list beats an empty one.
        await StopAsync(clearTasks: false).ConfigureAwait(false);
        await StartAsync(ct).ConfigureAwait(false);
        return ConnectionState == HubConnectionState.Connected
            ? ReconnectOutcome.Connected
            : ReconnectOutcome.Retrying;
    }

    /// <summary>What a manual reconnect achieved, so the dialog can say something truthful.</summary>
    public enum ReconnectOutcome
    {
        /// <summary>The hub is up again.</summary>
        Connected,
        /// <summary>The attempt failed; the automatic loop is still trying.</summary>
        Retrying,
        /// <summary>No session left to connect with; the user has to sign in again.</summary>
        SessionExpired
    }

    /// <summary>
    /// Tears down the hub. <paramref name="clearTasks"/> defaults to true for the logout/dispose path;
    /// pass false ahead of a reconnect attempt so the visible task list survives instead of flashing
    /// empty for the duration of the attempt.
    /// </summary>
    public async Task StopAsync(bool clearTasks = true)
    {
        _startRetryCts?.Cancel();
        _startRetryCts?.Dispose();
        _startRetryCts = null;
        var hub = _hub;
        _hub = null;
        if (hub is not null)
        {
            try { await hub.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "[TaskTracker] hub dispose error"); }
        }
        if (clearTasks)
        {
            lock (_gate) _byId.Clear();
            OnChanged?.Invoke();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Shape of the TaskStarted SignalR payload - see TaskService.StartTaskAsync.</summary>
    private sealed record TaskStartedEvent(int TaskId, int ServerId, DateTime? StartedAt);

    /// <summary>
    /// Publishes each reconnect attempt's delay via <see cref="OnReconnectAttempt"/> so the UI can show
    /// a live "reconnect in N" countdown. Retries indefinitely, clamping to the last (30s) delay, so a
    /// backend that is down for a while is auto-rejoined the moment it returns - the connection-lost
    /// dialog then closes on its own (matches the Astraia behaviour).
    /// </summary>
    private sealed class NotifyingRetryPolicy(TaskTrackerService owner) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = (int)Math.Min(retryContext.PreviousRetryCount, RetryDelays.Count - 1);
            var delay = RetryDelays[index];
            owner.OnReconnectAttempt?.Invoke((int)delay.TotalSeconds);
            return delay;
        }
    }
}
