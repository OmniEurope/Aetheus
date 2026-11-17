// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// R-36, loading level 3: a veil over one component while the rest of the page is loaded. It never covers more than the
/// area it is declared in. Level 2, the whole content area, is <c>MainLayout</c>'s veil (<see cref="BusyVeil"/>,
/// <see cref="VeilHold"/>); level 1, the whole site, is the boot splash.
/// </summary>
public partial class ZoneVeil
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>True while the component waits; the veil shows after OE's 100 ms delay, so a short wait shows nothing.</summary>
    [Parameter] public bool Busy { get; set; } = true;

    /// <summary>What is announced while the veil is up; the localized "Loading" when null.</summary>
    [Parameter] public string? Label { get; set; }
}
