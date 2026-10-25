// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

internal sealed class ArtifactTransferBudget
{
    private const long HeavyThresholdBytes = 128L * 1024 * 1024;
    private const int MaximumLightTransfers = 2;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _changed = new(0, int.MaxValue);
    private bool _heavyRunning;
    private int _lightRunning;

    internal static ArtifactTransferBudget Shared { get; } = new();

    internal async Task<IAsyncDisposable> AcquireAsync(long bytes, CancellationToken ct)
    {
        var heavy = bytes >= HeavyThresholdBytes;
        while (true)
        {
            lock (_sync)
            {
                if (heavy ? !_heavyRunning && _lightRunning == 0 : !_heavyRunning && _lightRunning < MaximumLightTransfers)
                {
                    if (heavy) _heavyRunning = true;
                    else _lightRunning++;
                    return new Reservation(() => Release(heavy));
                }
            }
            await _changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void Release(bool heavy)
    {
        lock (_sync)
        {
            if (heavy) _heavyRunning = false;
            else _lightRunning--;
        }
        _changed.Release();
    }

    /// <summary>Gives the slot back once, through the callback it is created with, so it does not depend on
    /// the budget that hands it out.</summary>
    private sealed class Reservation(Action release) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) release();
            return ValueTask.CompletedTask;
        }
    }
}
