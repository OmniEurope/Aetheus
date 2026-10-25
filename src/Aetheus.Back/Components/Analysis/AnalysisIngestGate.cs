// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis;

public sealed class AnalysisIngestGate
{
    private readonly object _sync = new();
    private readonly Dictionary<int, RunGate> _locks = [];
    private readonly SemaphoreSlim _global;

    public AnalysisIngestGate(Microsoft.Extensions.Options.IOptions<AnalysisPlatformOptions> options)
    {
        var maximum = Math.Clamp(options.Value.MaxConcurrentIngestions, 1, 64);
        _global = new SemaphoreSlim(maximum, maximum);
    }

    public async Task<IAsyncDisposable> EnterAsync(int runId, CancellationToken ct)
    {
        RunGate gate;
        lock (_sync)
        {
            if (!_locks.TryGetValue(runId, out gate!))
            {
                gate = new RunGate();
                _locks.Add(runId, gate);
            }
            gate.References++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            ReleaseReference(runId, gate);
            throw;
        }

        try
        {
            await _global.WaitAsync(ct).ConfigureAwait(false);
            return new Lease(_global, gate.Semaphore, () => ReleaseReference(runId, gate));
        }
        catch
        {
            gate.Semaphore.Release();
            ReleaseReference(runId, gate);
            throw;
        }
    }

    private void ReleaseReference(int runId, RunGate gate)
    {
        lock (_sync)
        {
            gate.References--;
            if (gate.References == 0
                && _locks.TryGetValue(runId, out var current)
                && ReferenceEquals(current, gate))
                _locks.Remove(runId);
        }
    }

    private sealed class RunGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    /// <summary>The two slots an ingestion holds, given back once. It reaches the gate only through the
    /// callback it is created with, so the gate and its lease do not depend on each other.</summary>
    private sealed class Lease(SemaphoreSlim global, SemaphoreSlim runSemaphore, Action releaseReference) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                global.Release();
                runSemaphore.Release();
                releaseReference();
            }
            return ValueTask.CompletedTask;
        }
    }
}
