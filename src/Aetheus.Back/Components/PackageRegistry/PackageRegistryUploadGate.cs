// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.PackageRegistry;

public sealed class PackageRegistryUploadGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IDisposable? TryEnter() =>
        _gate.Wait(0) ? new GateLease(_gate) : null;

    /// <summary>Waits up to <paramref name="timeout"/> for the single upload slot. Maven clients put
    /// a version's files in parallel, so refusing at once would fail an ordinary deploy; the slot
    /// still bounds the uploads the registry stores at the same time.</summary>
    public async Task<IDisposable?> EnterAsync(TimeSpan timeout, CancellationToken ct)
        => await _gate.WaitAsync(timeout, ct).ConfigureAwait(false) ? new GateLease(_gate) : null;

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
