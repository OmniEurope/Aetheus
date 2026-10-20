// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Owns cancellation and generation tracking for route-scoped pipeline run loads.</summary>
internal sealed class PipelineRunLoadSession : IDisposable
{
    private CancellationTokenSource? _current;
    private int _generation;
    private bool _disposed;

    /// <summary>After disposal, return a cancelled token rather than accessing the disposed source.
    /// Pending HTTP continuations then stop their remaining loads safely.</summary>
    public CancellationToken Token => _disposed ? new CancellationToken(canceled: true) : _current?.Token ?? default;

    public (int Generation, CancellationToken Token) Begin()
    {
        if (_disposed) return (++_generation, new CancellationToken(canceled: true));
        _current?.Cancel();
        _current?.Dispose();
        _current = new CancellationTokenSource();
        return (++_generation, _current.Token);
    }

    /// <summary>False once the session is disposed: a load resumed after the run page was left must
    /// not open the live connection, which nothing would dispose any more.</summary>
    public bool IsCurrent(int generation) => !_disposed && generation == _generation;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current?.Cancel();
        _current?.Dispose();
        _current = null;
    }
}
