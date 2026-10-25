// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Services;

internal sealed class BufferedLogForwarder(
    int taskId,
    IServerApiClient apiClient,
    TimeProvider timeProvider,
    CancellationToken taskCancellation) : IAsyncDisposable
{
    private const int BatchSize = 20;
    private const int MaxBufferedLines = 2000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private readonly List<AppendLogRequest> _buffer = [];
    private readonly object _lock = new();
    private readonly CancellationTokenSource _timerCts = new();
    private Task? _timerLoop;
    private int _droppedLines;

    public async Task OnOutputAsync(string message, TaskLogLevel level)
    {
        var log = new AppendLogRequest { TaskId = taskId, Level = level, Message = message };
        List<AppendLogRequest>? batch = null;
        lock (_lock)
        {
            // Lazily start the periodic flush on first output: a step slower than the
            // 20-line threshold would otherwise show nothing live until it finishes.
            _timerLoop ??= TimerLoopAsync();
            _buffer.Add(log);
            if (_buffer.Count >= BatchSize)
                batch = TakeBufferLocked();
        }
        if (batch is not null)
            await TrySendAsync(batch, taskCancellation).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken flushCt)
    {
        // Stop the periodic timer first: after this returns, the terminal flush is the sole writer.
        await StopTimerAsync().ConfigureAwait(false);
        List<AppendLogRequest>? remaining;
        lock (_lock)
        {
            remaining = _buffer.Count > 0 || _droppedLines > 0 ? TakeBufferLocked() : null;
        }
        if (remaining is not null)
            await SendLogBatchAsync(apiClient, remaining, flushCt, SendTimeout)
                .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // PollingService publishes terminal diagnostics independently. Disposal must never
            // replace the original executor exception with a best-effort log failure.
        }
        _timerCts.Dispose();
    }

    private async Task StopTimerAsync()
    {
        if (!_timerCts.IsCancellationRequested)
            await _timerCts.CancelAsync().ConfigureAwait(false);
        if (_timerLoop is null)
            return;
        try
        {
            await _timerLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal timer shutdown.
        }
    }

    private async Task TimerLoopAsync()
    {
        while (!_timerCts.IsCancellationRequested)
        {
            await Task.Delay(FlushInterval, timeProvider, _timerCts.Token).ConfigureAwait(false);
            List<AppendLogRequest>? pending;
            lock (_lock)
            {
                pending = _buffer.Count > 0 ? TakeBufferLocked() : null;
            }
            if (pending is not null)
                await TrySendAsync(pending, taskCancellation).ConfigureAwait(false);
        }
    }

    // Log forwarding is best-effort: a backend blip must never fail a real build.
    // Failed batches are re-queued with a bounded outage buffer.
    private async Task TrySendAsync(List<AppendLogRequest> batch, CancellationToken token)
    {
        try
        {
            await SendLogBatchAsync(apiClient, batch, token, SendTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            lock (_lock)
            {
                _buffer.InsertRange(0, batch);
                var overflow = _buffer.Count - MaxBufferedLines;
                if (overflow > 0)
                {
                    _buffer.RemoveRange(0, overflow);
                    _droppedLines += overflow;
                }
            }
        }
    }

    private List<AppendLogRequest> TakeBufferLocked()
    {
        List<AppendLogRequest> batch = [.. _buffer];
        _buffer.Clear();
        if (_droppedLines <= 0)
            return batch;
        batch.Insert(0, new AppendLogRequest
        {
            TaskId = taskId,
            Level = TaskLogLevel.Warning,
            Message = $"[agent] {_droppedLines} log line(s) dropped while the backend was unreachable"
        });
        _droppedLines = 0;
        return batch;
    }

    internal static async Task SendLogBatchAsync(
        IServerApiClient apiClient,
        List<AppendLogRequest> batch,
        CancellationToken token,
        TimeSpan sendTimeout)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(sendTimeout);
        await apiClient.AppendLogBatchAsync(batch, timeout.Token).ConfigureAwait(false);
    }
}
