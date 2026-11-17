// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// What <see cref="BusyVeil.Hold"/> hands back: disposing it releases the hold once, however many times it is
/// disposed. It knows only the <see cref="Action"/> that releases the hold, not the veil, so the two types do
/// not depend on each other.
/// </summary>
internal sealed class VeilHoldHandle(Action release) : IDisposable
{
    private Action? _release = release;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
