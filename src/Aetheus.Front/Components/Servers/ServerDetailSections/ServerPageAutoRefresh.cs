// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R-17: a server page that must not show a stale state (Apache after a restart done on the host) asks for it
/// again on its own, every <see cref="Interval"/>, while the page is open. A tick is dropped when the previous refresh is
/// still running (refreshes never stack) or when <c>skip</c> says so (a task of the page is in progress: its follower
/// waits for the server's next state, which a read of the backend's older copy must not end early). Disposing stops the
/// timer, so leaving the page leaves no refresh behind.
/// </summary>
internal sealed class ServerPageAutoRefresh : IDisposable
{
    /// <summary>The cadence the owner asked for: of the order of 10 to 20 seconds.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly Func<bool> _skip;
    private readonly Func<Task> _refresh;
    private readonly ITimer _timer;
    private int _running;
    private bool _disposed;

    public ServerPageAutoRefresh(TimeProvider clock, Func<bool> skip, Func<Task> refresh)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _skip = skip;
        _refresh = refresh;
        _timer = clock.CreateTimer(_ => _ = TickAsync(), null, Interval, Interval);
    }

    /// <summary>One tick: runs the refresh unless the page is gone, a refresh is already running, or the page asks to skip.</summary>
    internal async Task TickAsync()
    {
        if (_disposed || _skip()) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            await _refresh().ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The next tick tries again; the page keeps what it shows.
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
