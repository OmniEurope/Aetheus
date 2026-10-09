// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Owns cancellation and generation tracking for route-scoped pipeline run loads.</summary>
internal sealed class PipelineRunLoadSession : IDisposable
{
    private CancellationTokenSource? _current;
    private int _generation;
    private bool _disposed;

    /// <summary>
    /// The current load's token. Once the session is disposed (the run page was left) it is a cancelled
    /// token, never the disposed source's: an Approve, Reject, Cancel or Retry whose HTTP call returned
    /// after the page was left read it and threw ObjectDisposedException into the error boundary
    /// (recette R2-100). A cancelled token stops the remaining loads instead.
    /// </summary>
    public CancellationToken Token => _disposed ? new CancellationToken(canceled: true) : _current?.Token ?? default;

    public (int Generation, CancellationToken Token) Begin()
    {
        if (_disposed) return (++_generation, new CancellationToken(canceled: true));
        _current?.Cancel();
        _current?.Dispose();
        _current = new CancellationTokenSource();
        return (++_generation, _current.Token);
    }

    /// <summary>False once the session is disposed: a load resumed after the run page was left must not
    /// open the live connection, which nothing would dispose any more (recette R2-100).</summary>
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
