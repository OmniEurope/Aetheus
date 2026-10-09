// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Live log streaming (S-TECH-32) for the selected running step, with a 2s poll fallback. Extracted
/// from the former <c>PipelineRun.Logs.cs</c> partial into a real collaborator: it owns the log hub
/// connection, the streaming task id and the poll timer, while the component keeps the shared
/// <paramref name="logs"/> (read by the rest of the view) and supplies UI-refresh callbacks. FRONT-010:
/// streamed and polled lines go through the window, which keeps them bounded.
/// </summary>
internal sealed class PipelineRunLogStreamer(
    ApiClient api,
    HubConnectionFactory hubFactory,
    ILogger logger,
    PipelineRunLogWindow logs,
    Func<Func<Task>, Task> invokeAsync,
    Action stateHasChanged) : IAsyncDisposable
{
    private HubConnection? _logHub;
    private int? _streamingTaskId;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private int _selectionGeneration;
    private bool _disposed;
    private PipelineStepRunDto? _selectedStep;

    /// <summary>Re-evaluates streaming for the currently-selected step. Call after the selection or a
    /// step's status changes.</summary>
    public async Task UpdateAsync(PipelineStepRunDto? selectedStep)
    {
        if (_disposed) return;
        _selectedStep = selectedStep;
        await UpdateLogStreamingAsync();
    }

    private void StartPolling(int taskId, int generation)
    {
        if (_disposed) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _pollCts = cts;
        _pollTask = PollLoopAsync(taskId, generation, cts);
    }

    private async Task PollLoopAsync(int taskId, int generation, CancellationTokenSource cts)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cts.Token))
                await PollTaskLogsAsync(taskId, generation, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_pollCts, cts))
            {
                _pollCts = null;
                _pollTask = null;
            }
            cts.Dispose();
        }
    }

    private async Task PollTaskLogsAsync(int taskId, int generation, CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct);
        try
        {
            var tail = await api.Monitoring.GetTaskLogTailAsync(taskId, PipelineRunLogWindow.LiveWindow, ct);
            if (_disposed || generation != _selectionGeneration || _streamingTaskId != taskId) return;
            await invokeAsync(() =>
            {
                if (_disposed || generation != _selectionGeneration || _streamingTaskId != taskId)
                    return Task.CompletedTask;
                if (logs.ApplySnapshot(taskId, tail, PipelineRunLogWindow.LiveWindow))
                    stateHasChanged();
                return Task.CompletedTask;
            });
        }
        catch (HttpRequestException) { /* ignore transient failures */ }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task StopPollingAsync()
    {
        var cts = _pollCts;
        var task = _pollTask;
        cts?.Cancel();
        if (task is not null)
            await task;
    }

    // S-TECH-32: subscribe to the log hub's task group for the selected running step so new lines stream
    // in live. Falls back to the 2s poll if the hub can't be reached. Only one task is streamed at a time.
    private async Task UpdateLogStreamingAsync()
    {
        var runningTaskId = _selectedStep is { Status: TaskExecutionStatus.Running, TaskId: { } tid } ? tid : (int?)null;
        if (runningTaskId == _streamingTaskId) return;
        var generation = Interlocked.Increment(ref _selectionGeneration);
        await StopPollingAsync();

        await LeaveCurrentTaskGroupAsync();
        _streamingTaskId = runningTaskId;

        if (runningTaskId is not { } taskId)
            return;

        try
        {
            EnsureLogHub();
            if (_logHub!.State != HubConnectionState.Connected)
                await _logHub.StartAsync();
            if (_disposed || generation != _selectionGeneration || _streamingTaskId != taskId) return;
            await _logHub.InvokeAsync("JoinTaskGroup", taskId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[PipelineRun] Log streaming unavailable - falling back to polling");
            if (!_disposed && generation == _selectionGeneration && _streamingTaskId == taskId)
                StartPolling(taskId, generation);
        }
    }

    private async Task LeaveCurrentTaskGroupAsync()
    {
        if (_streamingTaskId is not { } taskId || _logHub is not { State: HubConnectionState.Connected }) return;
        try { await _logHub.InvokeAsync("LeaveTaskGroup", taskId); }
        catch (Exception ex) { logger.LogDebug(ex, "[PipelineRun] Leave log group failed"); }
    }

    private void EnsureLogHub()
    {
        if (_logHub is not null) return;
        _logHub = hubFactory.Create("logs");
        _logHub.On<TaskLogDto>("LogReceived", log => invokeAsync(() => AppendStreamedLogsAsync([log])));
        _logHub.On<List<TaskLogDto>>("LogsReceived", logs => invokeAsync(() => AppendStreamedLogsAsync(logs)));
        _logHub.RejoinOnReconnect(async () =>
        {
            if (!_disposed && _streamingTaskId is { } taskId)
                await _logHub.InvokeAsync("JoinTaskGroup", taskId);
        });
    }

    private Task AppendStreamedLogsAsync(IEnumerable<TaskLogDto> streamed)
    {
        if (_disposed || _streamingTaskId is not { } taskId) return Task.CompletedTask;
        var own = streamed.Where(log => log.TaskId == taskId).ToList();
        if (own.Count > 0 && logs.AppendStreamed(taskId, own)) stateHasChanged();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();
        await StopPollingAsync();
        if (_logHub is not null)
        {
            if (_streamingTaskId is { } taskId && _logHub.State == HubConnectionState.Connected)
            {
                try { await _logHub.InvokeAsync("LeaveTaskGroup", taskId); }
                catch (Exception ex) { logger.LogDebug(ex, "[PipelineRun] Leave log group failed"); }
            }
            await _logHub.DisposeAsync();
        }
        _lifetimeCts.Dispose();
        _pollGate.Dispose();
    }
}
