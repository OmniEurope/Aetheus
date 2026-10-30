// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Aetheus.Agent.Core.Services;

public sealed class AgentRuntimeHealth(TimeProvider timeProvider)
{
    private long _lastHeartbeatSuccessUtcTicks;
    private long _lastPollingSuccessUtcTicks;
    private int _activeTaskCount;
    private long _processRegistrationSequence;
    private readonly ConcurrentDictionary<long, ProcessIdentity> _activeProcesses = new();

    public void BeginHeartbeatGracePeriod() => MarkHeartbeatSuccess();

    public void BeginPollingGracePeriod() => MarkPollingSuccess();

    public void MarkHeartbeatSuccess() =>
        Interlocked.Exchange(ref _lastHeartbeatSuccessUtcTicks, timeProvider.GetUtcNow().UtcTicks);

    public void MarkPollingSuccess() =>
        Interlocked.Exchange(ref _lastPollingSuccessUtcTicks, timeProvider.GetUtcNow().UtcTicks);

    // A service or package action has just changed what the host runs. The heartbeat loop sends the
    // new state now rather than at its next tick, up to thirty seconds later, so the Services page
    // shows the result of the action it just ran. One pending request at most.
    private readonly SemaphoreSlim _heartbeatRequested = new(0, 1);
    private readonly Lock _heartbeatRequestGate = new();

    /// <summary>Asks the heartbeat loop for a beat as soon as it is free.</summary>
    public void RequestHeartbeat()
    {
        lock (_heartbeatRequestGate)
        {
            if (_heartbeatRequested.CurrentCount == 0)
                _heartbeatRequested.Release();
        }
    }

    /// <summary>Completes when a beat has been asked for; consumes the request.</summary>
    internal Task WaitForHeartbeatRequestAsync(CancellationToken ct) => _heartbeatRequested.WaitAsync(ct);

    // Recette R-119: an operation (a mail change, an Apache site enabled or deleted...) has just changed what a
    // cached inventory reports. The heartbeat drops that cached inventory before the next beat, so the page, and a
    // reload, read the real state instead of the one collected up to SlowInventoryTtl ago.
    private int _staleInventories;

    /// <summary>Marks the cached <paramref name="inventories"/> as outdated; the next beat collects them again.</summary>
    public void InvalidateInventories(CachedInventory inventories) =>
        Interlocked.Or(ref _staleInventories, (int)inventories);

    /// <summary>The inventories marked since the last call; consumes the marks.</summary>
    internal CachedInventory ConsumeInventoryInvalidations() =>
        (CachedInventory)Interlocked.Exchange(ref _staleInventories, 0);

    public void BeginTask() => Interlocked.Increment(ref _activeTaskCount);

    public void EndTask() => Interlocked.Decrement(ref _activeTaskCount);

    public int ActiveTaskCount => Math.Max(0, Volatile.Read(ref _activeTaskCount));

    // R-33: when this agent's pipeline tasks ran. A container is attributed to a run only on this record,
    // which the agent writes itself: labels and names are chosen by whatever started the container.
    private readonly Lock _pipelineWindowsGate = new();
    private readonly Dictionary<long, PipelineTaskWindow> _pipelineWindows = [];
    private long _pipelineWindowSequence;

    /// <summary>Records that a pipeline task runs from now until the returned handle is disposed.</summary>
    internal IDisposable TrackPipelineTask()
    {
        lock (_pipelineWindowsGate)
        {
            var id = ++_pipelineWindowSequence;
            _pipelineWindows[id] = new PipelineTaskWindow(timeProvider.GetUtcNow(), null);
            return new PipelineTaskRegistration(() => EndPipelineTask(id));
        }
    }

    /// <summary>True when a pipeline task of this agent was already running at <paramref name="createdAt"/>
    /// and is still running, or ended no longer than <see cref="AgentRuntimeDefaults.PipelineContainerGrace"/>
    /// ago (a reaper outlives its test process by a few seconds).</summary>
    public bool CreatedDuringLivePipelineTask(DateTimeOffset createdAt)
    {
        lock (_pipelineWindowsGate)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var (id, window) in _pipelineWindows.ToList())
            {
                if (window.End is { } ended && now - ended > AgentRuntimeDefaults.PipelineContainerGrace)
                    _pipelineWindows.Remove(id);
            }
            return _pipelineWindows.Values.Any(window =>
                window.Start <= createdAt && (window.End is null || createdAt <= window.End));
        }
    }

    private void EndPipelineTask(long id)
    {
        lock (_pipelineWindowsGate)
        {
            if (_pipelineWindows.TryGetValue(id, out var window) && window.End is null)
                _pipelineWindows[id] = window with { End = timeProvider.GetUtcNow() };
        }
    }

    private sealed record PipelineTaskWindow(DateTimeOffset Start, DateTimeOffset? End);

    private sealed class PipelineTaskRegistration(Action end) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                end();
        }
    }

    public int ActiveProcessCount
    {
        get
        {
            RemoveExitedProcesses();
            return _activeProcesses.Count;
        }
    }

    public bool IsIdle => ActiveTaskCount == 0 && ActiveProcessCount == 0;

    /// <summary>The processes still counted as running, one line each (PID, name, exited, recorded and current
    /// start time): what a test prints when the count does not settle, so the failure names the survivor.</summary>
    internal IReadOnlyList<string> DescribeActiveProcesses() =>
        [.. _activeProcesses.Values.Select(Describe)];

    private static string Describe(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return $"PID {identity.ProcessId} {process.ProcessName} exited={process.HasExited} recorded={identity.StartTimeUtcTicks} current={ReadStartTimeTicks(process)}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return $"PID {identity.ProcessId} recorded={identity.StartTimeUtcTicks} unreadable: {ex.GetType().Name} {ex.Message}";
        }
    }

    internal IDisposable TrackProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var registrationId = Interlocked.Increment(ref _processRegistrationSequence);
        _activeProcesses[registrationId] = new ProcessIdentity(process.Id, ReadStartTimeTicks(process));
        return new ProcessRegistration(_activeProcesses, registrationId);
    }

    public DateTimeOffset? LastHeartbeatSuccessAt => Read(ref _lastHeartbeatSuccessUtcTicks);

    public DateTimeOffset? LastPollingSuccessAt => Read(ref _lastPollingSuccessUtcTicks);

    private static DateTimeOffset? Read(ref long ticks)
    {
        var value = Volatile.Read(ref ticks);
        return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
    }

    private void RemoveExitedProcesses()
    {
        foreach (var registration in _activeProcesses)
        {
            if (!IsRunning(registration.Value))
                _activeProcesses.TryRemove(registration.Key, out _);
        }
    }

    private static bool IsRunning(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
                return false;
            return identity.StartTimeUtcTicks == 0
                || ReadStartTimeTicks(process) == identity.StartTimeUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private static long ReadStartTimeTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return 0;
        }
    }

    private sealed class ProcessRegistration(
        ConcurrentDictionary<long, ProcessIdentity> activeProcesses, long registrationId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                activeProcesses.TryRemove(registrationId, out _);
        }
    }

    private readonly record struct ProcessIdentity(int ProcessId, long StartTimeUtcTicks);
}
