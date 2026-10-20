// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Which server-pushed events make a view of one server reload.</summary>
public enum ServerLiveFeedTriggers
{
    /// <summary>
    /// Agent heartbeats only (one every ~30 s). Required when loading the view itself queues an agent
    /// task: reloading on that task's completion would queue the next one, forever.
    /// </summary>
    Heartbeat,

    /// <summary>Heartbeats plus every completed task of the server (its results may be what the view shows).</summary>
    HeartbeatAndTasks
}

/// <summary>Follow server heartbeats and optional task completion with coalesced reloads so views
/// track the inventory without overlapping requests; rejoin and reload after reconnect, preserving
/// the last state when the hub is unavailable.</summary>
public sealed class ServerLiveFeed(
    HubConnectionFactory hubFactory,
    Func<int, CancellationToken, Task>? coalesceDelay = null) : IAsyncDisposable
{
    /// <summary>Debounce window: the heartbeat and task pushes of one agent beat land together.</summary>
    internal const int CoalesceWindowMs = 300;

    private readonly TrailingReloadCoalescer _coalescer = new(CoalesceWindowMs, coalesceDelay);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _hub;
    private int? _serverId;
    private ServerLiveFeedTriggers _triggers;
    private Func<Task>? _onChanged;
    private bool _disposed;

    /// <summary>The server whose pushes are followed, or null before the first start.</summary>
    public int? ServerId => _serverId;

    /// <summary>
    /// Follows <paramref name="serverId"/>. Calling it again for another server leaves the previous
    /// connection first; for the same server it only updates the triggers and the callback.
    /// </summary>
    public async Task StartAsync(int serverId, ServerLiveFeedTriggers triggers, Func<Task> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        if (_disposed) return;
        try
        {
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return; // disposed meanwhile: the view is gone
        }

        try
        {
            _triggers = triggers;
            _onChanged = onChanged;
            if (_serverId == serverId && _hub is not null) return;

            await StopHubAsync().ConfigureAwait(false);
            _serverId = serverId;
            var hub = hubFactory.Create("servers");
            // Not awaited: the SignalR client runs handlers one after another, a reload in flight must
            // not hold back the next message (the coalescer absorbs it instead).
            hub.On<int, ServerHeartbeatDto>("ServerHeartbeat", (id, heartbeat) =>
            {
                _ = OnHeartbeatAsync(id);
                return Task.CompletedTask;
            });
            hub.On<TaskCompletedNotification>("TaskCompleted", notification =>
            {
                _ = OnTaskCompletedAsync(notification);
                return Task.CompletedTask;
            });
            hub.RejoinOnReconnect(async () =>
            {
                await hub.InvokeAsync("JoinServerGroup", serverId).ConfigureAwait(false);
                await RequestReloadAsync().ConfigureAwait(false);
            });
            _hub = hub;

            using var deadline = new CancellationTokenSource(FrontendRuntimeDefaults.SignalRStartTimeout, hubFactory.Clock);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, deadline.Token);
            await hub.StartAsync(timeout.Token).ConfigureAwait(false);
            await hub.InvokeAsync("JoinServerGroup", serverId, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the view keeps its last load; nothing here may fault the page.
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>A heartbeat push. Reloads when it concerns the followed server.</summary>
    internal Task OnHeartbeatAsync(int serverId) =>
        serverId == _serverId ? RequestReloadAsync() : Task.CompletedTask;

    /// <summary>A completed-task push. Reloads only when tasks are a trigger and it is the followed server's.</summary>
    internal Task OnTaskCompletedAsync(TaskCompletedNotification notification) =>
        _triggers == ServerLiveFeedTriggers.HeartbeatAndTasks && notification.ServerId == _serverId
            ? RequestReloadAsync()
            : Task.CompletedTask;

    private Task RequestReloadAsync() =>
        _disposed ? Task.CompletedTask : _coalescer.RequestBestEffortAsync(_onChanged, _lifetime.Token);

    private async Task StopHubAsync()
    {
        var hub = _hub;
        _hub = null;
        if (hub is null) return;
        try { await hub.DisposeAsync().ConfigureAwait(false); }
        catch { /* a broken transport is already gone */ }
    }

    private void ReleaseGate()
    {
        try { _gate.Release(); }
        catch (ObjectDisposedException) { } // disposed while starting
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopHubAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _lifetime.Dispose();
        }
    }
}
