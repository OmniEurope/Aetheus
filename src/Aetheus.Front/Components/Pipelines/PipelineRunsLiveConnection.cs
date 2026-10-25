// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Owns the pipelines-hub subscription used by the pipeline edit page. It tracks the active run
/// groups, restores them after reconnect, and coalesces event bursts into one serialized reload.
/// </summary>
internal sealed class PipelineRunsLiveConnection(
    HubConnectionFactory hubFactory,
    ILogger logger,
    int pipelineId,
    Func<IReadOnlyCollection<int>> activeRunIds,
    Func<Task> onReload,
    Func<Func<Task>, Task> invokeAsync) : IAsyncDisposable
{
    private readonly DebouncedAsyncAction _reload = new(onReload, logger, "[PipelineEdit]");
    private readonly HashSet<int> _joinedRunGroups = [];
    private HubConnection? _hub;
    private bool _disposed;

    public async Task StartAsync()
    {
        if (_disposed) return;
        _hub = hubFactory.Create("pipelines");
        _hub.On<int, int>("PipelineRunStarted", (_, eventPipelineId) =>
            eventPipelineId == pipelineId ? invokeAsync(ScheduleReloadAsync) : Task.CompletedTask);
        _hub.On<int, PipelineStatus>("PipelineRunCompleted", (_, _) => invokeAsync(ScheduleReloadAsync));
        _hub.On<int>("PipelineRunCancelled", _ => invokeAsync(ScheduleReloadAsync));
        _hub.On<int, DateTime>("PipelineRunAwaitingResume", (_, _) => invokeAsync(ScheduleReloadAsync));
        _hub.On<int>("StepStarted", _ => invokeAsync(ScheduleReloadAsync));
        _hub.On<int, TaskExecutionStatus>("StepCompleted", (_, _) => invokeAsync(ScheduleReloadAsync));
        _hub.On<int, string, string>("ApprovalRequired", (_, _, _) => invokeAsync(ScheduleReloadAsync));
        _hub.On<int, string, string>("ApprovalResolved", (_, _, _) => invokeAsync(ScheduleReloadAsync));
        _hub.RejoinOnReconnect(() => invokeAsync(async () =>
        {
            if (_disposed || _hub is null) return;
            await _hub.InvokeAsync("JoinPipelineUpdatesGroup");
            _joinedRunGroups.Clear();
            await SyncGroupsAsync();
            await ScheduleReloadAsync();
        }));

        try
        {
            await _hub.StartAsync();
            await _hub.InvokeAsync("JoinPipelineUpdatesGroup");
            await SyncGroupsAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[PipelineEdit] Live updates unavailable");
        }
    }

    public async Task SyncGroupsAsync()
    {
        if (_disposed || _hub is not { State: HubConnectionState.Connected }) return;
        var desired = activeRunIds().ToHashSet();
        foreach (var runId in _joinedRunGroups.Where(id => !desired.Contains(id)).ToList())
        {
            try { await _hub.InvokeAsync("LeavePipelineRunGroup", runId); }
            catch (Exception ex) { logger.LogDebug(ex, "[PipelineEdit] Leave run group {RunId} failed", runId); }
            _joinedRunGroups.Remove(runId);
        }

        foreach (var runId in desired)
        {
            if (!_joinedRunGroups.Add(runId)) continue;
            try { await _hub.InvokeAsync("JoinPipelineRunGroup", runId); }
            catch (Exception ex)
            {
                _joinedRunGroups.Remove(runId);
                logger.LogDebug(ex, "[PipelineEdit] Join run group {RunId} failed", runId);
            }
        }
    }

    private Task ScheduleReloadAsync() => _reload.ScheduleAsync();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _reload.DisposeAsync();
        if (_hub is not null)
        {
            foreach (var runId in _joinedRunGroups.ToList())
            {
                try { await _hub.InvokeAsync("LeavePipelineRunGroup", runId); }
                catch (Exception ex) { logger.LogDebug(ex, "[PipelineEdit] Leave run group {RunId} failed", runId); }
            }
            _joinedRunGroups.Clear();
            await _hub.DisposeAsync();
        }

    }
}
