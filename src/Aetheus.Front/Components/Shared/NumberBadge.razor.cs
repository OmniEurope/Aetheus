// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Web;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class NumberBadge
{
    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter] public OmniTone Tone { get; set; } = OmniTone.Neutral;

    [Parameter] public OmniFill Fill { get; set; } = OmniFill.Solid;

    /// <summary>Common minimum width so the badges of one column line up; Auto keeps the width of the content.</summary>
    [Parameter] public OmniBadgeWidth Width { get; set; } = OmniBadgeWidth.Auto;

    /// <summary>Makes the badge one real button (a count that opens the list it counts). Not with <see cref="Href"/>.</summary>
    [Parameter] public EventCallback<MouseEventArgs> OnClick { get; set; }

    /// <summary>Makes the badge one real link. Not with <see cref="OnClick"/>.</summary>
    [Parameter] public string? Href { get; set; }

    /// <summary>The accessible name of a clickable badge, when its number alone does not say what it opens.</summary>
    [Parameter] public string? Label { get; set; }

    [Parameter] public string? Class { get; set; }

    [Parameter] public string? Id { get; set; }

    /// <summary>Extra attributes (title, data-testid, aria-label) passed to the badge.</summary>
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AdditionalAttributes { get; set; }
}
