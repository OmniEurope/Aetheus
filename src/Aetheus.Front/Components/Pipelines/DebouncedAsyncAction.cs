// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

internal sealed class DebouncedAsyncAction(
    Func<Task> action, ILogger logger, string logContext, TimeSpan? delay = null, TimeProvider? clock = null)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _pendingCancellation;
    private Task? _pendingTask;
    private bool _disposed;

    /// <summary>Once disposal starts, return a cancelled token without accessing the disposed
    /// source. The disposed flag is set before source cancellation and disposal.</summary>
    internal CancellationToken LifetimeToken => _disposed ? new CancellationToken(canceled: true) : _lifetime.Token;

    internal Task ScheduleAsync()
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            _pendingCancellation?.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _pendingCancellation = cancellation;
            _pendingTask = RunAsync(cancellation);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs the action now instead of after the debounce, superseding a pending run, and completes only
    /// once it has run: for a caller that must know when the reload is actually done (a reconnect,
    /// whose refetch the synchronising indicator waits for).
    /// </summary>
    internal async Task RunNowAsync()
    {
        CancellationToken lifetime;
        lock (_sync)
        {
            if (_disposed) return;
            _pendingCancellation?.Cancel();
            lifetime = _lifetime.Token;
        }
        var entered = false;
        try
        {
            await _gate.WaitAsync(lifetime);
            entered = true;
            if (!_disposed) await action();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "{Context} Live reload failed", logContext); }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    private async Task RunAsync(CancellationTokenSource cancellation)
    {
        var entered = false;
        try
        {
            await Task.Delay(delay ?? TimeSpan.FromMilliseconds(250), clock ?? TimeProvider.System, cancellation.Token);
            await _gate.WaitAsync(cancellation.Token);
            entered = true;
            if (!_disposed) await action();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "{Context} Live reload failed", logContext); }
        finally
        {
            if (entered) _gate.Release();
            lock (_sync)
            {
                if (ReferenceEquals(_pendingCancellation, cancellation))
                {
                    _pendingCancellation = null;
                    _pendingTask = null;
                }
            }
            cancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _pendingCancellation?.Cancel();
            pending = _pendingTask;
        }
        if (pending is not null) await pending;
        await _gate.WaitAsync();
        _gate.Release();
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
