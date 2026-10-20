// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Follow authorized resource events and coalesce reloads so push bursts do not overload
/// the view; rejoin and catch up after reconnect, while an unavailable hub leaves the last loaded
/// state visible.</summary>
public sealed class EntityOperationalFeed(
    HubConnectionFactory hubFactory,
    Func<int, CancellationToken, Task>? coalesceDelay = null) : IAsyncDisposable
{
    /// <summary>Debounce window: consecutive ingestion batches collapse into one reload.</summary>
    internal const int CoalesceWindowMs = 1000;

    private readonly TrailingReloadCoalescer _coalescer = new(CoalesceWindowMs, coalesceDelay);
    private readonly CancellationTokenSource _lifetime = new();
    private HubConnection? _hub;
    private int _resourceId;
    private Func<Task>? _onChanged;
    private bool _disposed;

    /// <summary>
    /// Follows <paramref name="eventName"/> for the resource <paramref name="resourceId"/> of
    /// <paramref name="type"/>. The first call opens the connection; later calls only retarget it
    /// (the view switched to another resource), without a new connection.
    /// </summary>
    public async Task StartAsync(ResourceType type, string eventName, int resourceId, Func<Task> onChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        Follow(resourceId, onChanged);
        if (_disposed || _hub is not null) return;
        try
        {
            var hub = hubFactory.Create("entities");
            hub.On<int>(eventName, id =>
            {
                _ = OnEventAsync(id);
                return Task.CompletedTask;
            });
            hub.RejoinOnReconnect(async () =>
            {
                await hub.InvokeAsync("JoinEntityUpdates", type).ConfigureAwait(false);
                await RequestReloadAsync().ConfigureAwait(false);
            });
            _hub = hub;
            using var deadline = new CancellationTokenSource(FrontendRuntimeDefaults.SignalRStartTimeout, hubFactory.Clock);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, deadline.Token);
            await hub.StartAsync(timeout.Token).ConfigureAwait(false);
            await hub.InvokeAsync("JoinEntityUpdates", type, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the view keeps its last load; nothing here may fault the page.
        }
    }

    private void Follow(int resourceId, Func<Task> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        _resourceId = resourceId;
        _onChanged = onChanged;
    }

    /// <summary>An event push. Reloads when it concerns the followed resource.</summary>
    internal Task OnEventAsync(int resourceId) =>
        resourceId == _resourceId ? RequestReloadAsync() : Task.CompletedTask;

    private Task RequestReloadAsync() =>
        _disposed ? Task.CompletedTask : _coalescer.RequestBestEffortAsync(_onChanged, _lifetime.Token);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        var hub = _hub;
        _hub = null;
        if (hub is not null)
        {
            try { await hub.DisposeAsync().ConfigureAwait(false); }
            catch { /* a broken transport is already gone */ }
        }
        _lifetime.Dispose();
    }
}
