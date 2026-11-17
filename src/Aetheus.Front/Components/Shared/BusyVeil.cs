// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// R-1, R-36: the veil of the content area, loading level 2 (level 1 is the boot splash, level 3 a <see cref="ZoneVeil"/>
/// over one component; see docs/architecture/omnieurope-ui-customizations.md, "Niveaux de chargement"). <c>MainLayout</c> draws OE's <c>OmniPageVeil</c> (a constant colour, the
/// site's animated logo at its centre) over the page content while <see cref="IsBusy"/>: while an action runs
/// through <see cref="RunAsync(Func{Task})"/>, or while a zone waits and holds it through <see cref="Hold"/>
/// (<see cref="VeilHold"/>). The content stays mounted under the veil, the user cannot act on it and waits for the end.
/// </summary>
/// <remarks>
/// The veil is shown (the layout re-rendered) before the action starts: a confirmation dialog returns without
/// a render of its own. It is lifted whatever the outcome, an exception included. Several actions or holds at once keep
/// it until the last one ends. No zone, dialog included, veils itself: it holds this one.
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

    /// <summary>
    /// Keeps the veil up until the returned handle is disposed (a zone that waits for something other than an action
    /// it runs itself, see <see cref="VeilHold"/>). Disposing twice lifts it once; it counts like a running action.
    /// </summary>
    public IDisposable Hold()
    {
        _running++;
        Changed?.Invoke();
        return new VeilHoldHandle(Lift);
    }

    /// <summary>The counter always comes back first: a listener that throws cannot keep the veil on.</summary>
    private void Lift()
    {
        _running--;
        Changed?.Invoke();
    }
}
