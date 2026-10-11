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

    // Recette R-508: a service or package action has just changed what the host runs. The heartbeat
    // loop sends the new state now rather than at its next tick, up to thirty seconds later, so the
    // Services page shows the result of the action it just ran. One pending request at most.
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

    // Recette R1-8: a mail operation has just changed the mail stack (certificate, OpenDKIM, domains,
    // accounts...). The heartbeat drops its cached mail inventory before the next beat, instead of
    // reporting the old one for up to SlowInventoryTtl.
    private int _mailInventoryStale;

    /// <summary>Marks the cached mail inventory as outdated; the next beat collects it again.</summary>
    public void InvalidateMailInventory() => Interlocked.Exchange(ref _mailInventoryStale, 1);

    /// <summary>True once per <see cref="InvalidateMailInventory"/>; consumes the mark.</summary>
    internal bool ConsumeMailInventoryInvalidation() => Interlocked.Exchange(ref _mailInventoryStale, 0) == 1;

    public void BeginTask() => Interlocked.Increment(ref _activeTaskCount);

    public void EndTask() => Interlocked.Decrement(ref _activeTaskCount);

    public int ActiveTaskCount => Math.Max(0, Volatile.Read(ref _activeTaskCount));

    public int ActiveProcessCount
    {
        get
        {
            RemoveExitedProcesses();
            return _activeProcesses.Count;
        }
    }

    public bool IsIdle => ActiveTaskCount == 0 && ActiveProcessCount == 0;

    internal IDisposable TrackProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var registrationId = Interlocked.Increment(ref _processRegistrationSequence);
        _activeProcesses[registrationId] = new ProcessIdentity(process.Id, ReadStartTimeTicks(process));
        return new ProcessRegistration(this, registrationId);
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

    private sealed class ProcessRegistration(AgentRuntimeHealth owner, long registrationId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner._activeProcesses.TryRemove(registrationId, out _);
        }
    }

    private readonly record struct ProcessIdentity(int ProcessId, long StartTimeUtcTicks);
}
