// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Holds the application's one veil (<see cref="BusyVeil"/>, drawn by OE's <c>OmniPageVeil</c> in the main layout)
/// while <see cref="Busy"/> is true. A zone that waits for something (a read, a task, a purge) declares
/// <c>&lt;VeilHold Busy="@amWaiting" /&gt;</c> instead of veiling itself: there is no second layer and no class to put
/// on the zone. It draws nothing; the hold is released when <see cref="Busy"/> turns false or the zone leaves the page.
/// </summary>
public sealed class VeilHold : ComponentBase, IDisposable
{
    private IDisposable? _hold;

    [Inject] private BusyVeil Veil { get; set; } = default!;

    /// <summary>True while the zone waits: the page veil is held for as long.</summary>
    [Parameter] public bool Busy { get; set; }

    protected override void OnParametersSet()
    {
        if (Busy && _hold is null)
            _hold = Veil.Hold();
        else if (!Busy)
            Release();
    }

    public void Dispose() => Release();

    private void Release()
    {
        _hold?.Dispose();
        _hold = null;
    }
}
