// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Owns the pipelines-hub connection for a run view: it joins the run's group (plus every loaded child
/// run group of an orchestration) and turns a StepStarted/StepCompleted broadcast into a debounced
/// reload of the parent component. Extracted from <see cref="PipelineRun"/> to keep that component under
/// the file-size budget; the component keeps the actual reload (<c>onReload</c>) since it mutates the
/// view state, and supplies the current child-run ids (<c>childRunIds</c>) so newly-triggered children
/// are joined on the next reload.
/// </summary>
internal sealed class PipelineRunLiveConnection(
    HubConnectionFactory hubFactory,
    ILogger logger,
    int runId,
    Func<IReadOnlyCollection<int>> childRunIds,
    Func<Task> onReload,
    Func<Task> onQueueRefresh,
    Func<Func<Task>, Task> invokeAsync,
    Func<bool>? needsQueuePolling = null,
    Func<bool>? runInProgress = null,
    Action? onLiveStateChanged = null,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    /// <summary>How long the live connection may be down before the page says so and reloads by itself.</summary>
    internal static readonly TimeSpan InterruptionThreshold = TimeSpan.FromSeconds(10);

    /// <summary>How often a run still in progress is reloaded while the live connection is down.</summary>
    internal static readonly TimeSpan FallbackReloadInterval = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset? _downSince;
    private DateTimeOffset _lastFallbackReload;
    private bool _shownInterrupted;

    /// <summary>
    /// Since when the live updates of this run are interrupted, once they have been down for
    /// <see cref="InterruptionThreshold"/>; null while they flow (a short blip shows nothing).
    /// </summary>
    public DateTimeOffset? InterruptedSince =>
        _downSince is { } since && _clock.GetUtcNow() - since >= InterruptionThreshold ? since : null;

    private HubConnection? _hub;
    // Run groups already joined: the parent run plus every loaded child run of an orchestration. A child
    // pipeline broadcasts its step events to ITS OWN run group, so without joining the children the parent
    // view only refreshed on a manual reload.
    private readonly HashSet<int> _joined = [];
    private readonly DebouncedAsyncAction _reload = new(onReload, logger, "[PipelineRun]");
    private Task? _queuePollTask;
    private bool _disposed;
    private bool _moving;

    public async Task StartAsync()
    {
        if (_disposed) return;
        _queuePollTask ??= PollQueueAsync();
        _hub = hubFactory.Create("pipelines");
        _hub.On<int, int>("PipelineRunStarted", (_, _) => invokeAsync(ScheduleReloadAsync));
        _hub.On<int, PipelineStatus>("PipelineRunCompleted", (_, _) => invokeAsync(ScheduleReloadAsync));
        _hub.On<int>("PipelineRunCancelled", _ => invokeAsync(ScheduleReloadAsync));
        _hub.On<int>("StepStarted", HandleStepStartedAsync);
        _hub.On<int, TaskExecutionStatus>("StepCompleted", (_, _) => invokeAsync(ScheduleReloadAsync));
        _hub.On<int, string, string>("ApprovalRequired", (_, _, _) => invokeAsync(ScheduleReloadAsync));
        _hub.On<int, string, string>("ApprovalResolved", (_, _, _) => invokeAsync(ScheduleReloadAsync));
        // Group membership is per-connection and lost on auto-reconnect - re-join every run group + reload.
        // The reload runs now and is awaited, not debounced: the page is only caught up once it is done,
        // which is what the synchronising indicator waits for (the phone put down during a run).
        _hub.RejoinOnReconnect(() => invokeAsync(async () =>
        {
            if (_disposed) return;
            _joined.Clear();
            await JoinGroupsAsync();
            await _reload.RunNowAsync();
        }));
        hubFactory.BackendReplaced += OnBackendReplaced;
        hubFactory.PageResumed += OnPageResumed;
        _hub.Reconnecting += _ => invokeAsync(() => { MarkDown(); return Task.CompletedTask; });
        _hub.Reconnected += _ => invokeAsync(() => { MarkUp(); return Task.CompletedTask; });
        _hub.Closed += _ => invokeAsync(() => { MarkDown(); return Task.CompletedTask; });

        try
        {
            await _hub.StartAsync();
            await JoinGroupsAsync();
        }
        catch (Exception ex)
        {
            // Hub unavailable: the page stays readable, says the live updates are interrupted once the
            // threshold passes, reloads a running run by itself and retries the connection (PollQueueAsync).
            logger.LogInformation(ex, "[PipelineRun] The live connection could not start");
            MarkDown();
        }
    }

    private void MarkDown() => _downSince ??= _clock.GetUtcNow();

    private void MarkUp()
    {
        _downSince = null;
        NotifyIfShownStateChanged();
    }

    private void NotifyIfShownStateChanged()
    {
        var shown = InterruptedSince is not null;
        if (shown == _shownInterrupted) return;
        _shownInterrupted = shown;
        onLiveStateChanged?.Invoke();
    }

    // 2026-10-06: back in the foreground (or online again), the run is fetched again whatever the socket
    // says: events pushed while the page was away are lost for good. A connection that is down entirely
    // (it never started) is restarted, which re-joins and reloads.
    private void OnPageResumed() => _ = invokeAsync(ResumeAsync);

    internal async Task ResumeAsync()
    {
        if (_disposed || _hub is not { } hub) return;
        if (hub.State == HubConnectionState.Disconnected)
        {
            await MoveToCurrentBackendAsync();
            if (hub.State == HubConnectionState.Connected) MarkUp();
            return;
        }
        await _reload.RunNowAsync();
    }

    // While the live connection is down, a run still in progress is reloaded every
    // FallbackReloadInterval, and a connection down entirely is retried at the same pace.
    private async Task FallBackWhileInterruptedAsync()
    {
        NotifyIfShownStateChanged();
        if (_disposed || InterruptedSince is null || runInProgress?.Invoke() != true) return;
        var now = _clock.GetUtcNow();
        if (now - _lastFallbackReload < FallbackReloadInterval) return;
        _lastFallbackReload = now;
        await ResumeAsync();
    }

    // A blue-green switch of Aetheus leaves this socket on the previous colour while the new one runs
    // the rest of the deployment (see HubConnectionFactory.BackendReplaced): the page froze on the last
    // step the old colour saw. Restart the same connection, re-join every run group, reload once.
    // _moving keeps a second signal from overlapping a restart still in progress.
    private void OnBackendReplaced() => _ = invokeAsync(MoveToCurrentBackendAsync);

    internal async Task MoveToCurrentBackendAsync()
    {
        if (_disposed || _moving || _hub is not { } hub) return;
        _moving = true;
        try
        {
            await hub.RestartOnCurrentBackendAsync(async () =>
            {
                if (_disposed) return;
                _joined.Clear();
                await JoinGroupsAsync();
                await ScheduleReloadAsync();
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PipelineRun] Moving the live connection to the new backend failed");
        }
        finally
        {
            _moving = false;
        }
    }

    private async Task PollQueueAsync()
    {
        // Recette R2-100: the token is read once, while the reload's lifetime is alive.
        var lifetime = _reload.LifetimeToken;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(lifetime))
            {
                await invokeAsync(() => needsQueuePolling?.Invoke() == true
                    ? onQueueRefresh()
                    : Task.CompletedTask);
                await invokeAsync(FallBackWhileInterruptedAsync);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "[PipelineRun] Queue polling failed"); }
    }

    // Join the parent run group plus every currently-loaded child run group. Idempotent: only groups not
    // already joined are (re)joined. Call after each reload, since a triggered child run only appears once
    // its trigger step has fired.
    public async Task JoinGroupsAsync()
    {
        if (_disposed || _hub is not { State: HubConnectionState.Connected }) return;
        var desired = new HashSet<int>(DesiredRunIds());
        foreach (var obsoleteId in _joined.Where(id => !desired.Contains(id)).ToList())
        {
            try
            {
                await _hub.InvokeAsync("LeavePipelineRunGroup", obsoleteId);
                _joined.Remove(obsoleteId);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[PipelineRun] Leave obsolete run group {Id} failed", obsoleteId);
            }
        }
        foreach (var id in desired)
        {
            if (!_joined.Add(id)) continue;
            try { await _hub.InvokeAsync("JoinPipelineRunGroup", id); }
            catch (Exception ex) { _joined.Remove(id); logger.LogDebug(ex, "[PipelineRun] Join run group {Id} failed", id); }
        }
    }

    internal Task HandleStepStartedAsync(int stepId)
    {
        _ = stepId;
        return invokeAsync(ScheduleReloadAsync);
    }

    internal IReadOnlyCollection<int> DesiredRunIds() =>
        new[] { runId }.Concat(childRunIds()).Distinct().ToArray();

    // Debounce: a burst of step events collapses to a single reload 250 ms after the last one.
    private Task ScheduleReloadAsync() => _reload.ScheduleAsync();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        hubFactory.BackendReplaced -= OnBackendReplaced;
        hubFactory.PageResumed -= OnPageResumed;
        await _reload.DisposeAsync();
        if (_queuePollTask is not null)
            await _queuePollTask;

        if (_hub is not null)
        {
            foreach (var joinedRunId in _joined.ToList())
            {
                try { await _hub.InvokeAsync("LeavePipelineRunGroup", joinedRunId); }
                catch (Exception ex) { logger.LogDebug(ex, "[PipelineRun] Leave run group {Id} failed", joinedRunId); }
            }
            _joined.Clear();
            await _hub.DisposeAsync();
        }

    }
}
