// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// R-99: the veil of an action in progress. The page body (<c>MainLayout</c>'s <c>app-body-wrapper</c>) takes
/// OE's <c>omni-busy</c> class and <c>aria-busy="true"</c> while an action runs through <see cref="RunAsync(Func{Task})"/>:
/// the content stays mounted under the veil, the user cannot act on it and waits for the end. A deletion, or any
/// action started from a confirmation, runs inside it, so the page never looks idle while the server works.
/// </summary>
/// <remarks>
/// The veil is shown (the layout re-rendered) before the action starts: a confirmation dialog returns without
/// a render of its own. It is lifted whatever the outcome, an exception included. Several actions at once keep
/// it until the last one ends. A dialog that runs its own action veils its own content instead (its own busy flag).
/// </remarks>
public sealed class BusyVeil
{
    private int _running;

    /// <summary>True while at least one action runs through <see cref="RunAsync(Func{Task})"/>.</summary>
    public bool IsBusy => _running > 0;

    /// <summary>Raised when <see cref="IsBusy"/> may have changed; the layout renders again.</summary>
    public event Action? Changed;

    /// <summary>Runs <paramref name="action"/> under the veil; the veil is lifted when it ends or fails.</summary>
    public async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _running++;
        try
        {
            Changed?.Invoke();
            await action();
        }
        finally
        {
            Lift();
        }
    }

    /// <summary>Same as <see cref="RunAsync(Func{Task})"/>, handing back what the action returns.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _running++;
        try
        {
            Changed?.Invoke();
            return await action();
        }
        finally
        {
            Lift();
        }
    }

    /// <summary>The counter always comes back first: a listener that throws cannot keep the veil on.</summary>
    private void Lift()
    {
        _running--;
        Changed?.Invoke();
    }
}
