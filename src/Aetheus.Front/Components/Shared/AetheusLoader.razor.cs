// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary> The product's loading indicator: OE's <see cref="OmniLogoLoader"/> with the Aetheus
/// plane. <see cref="Size"/> is OE's (1.5rem, 3rem, 5rem); <see cref="Centered"/> gives the loader
/// the width of its container, the mark in the middle, for a wait that stands in for a whole block.
/// </summary>
public partial class AetheusLoader
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public OmniControlSize Size { get; set; } = OmniControlSize.Medium;
    [Parameter] public bool Centered { get; set; } = true;
    [Parameter] public string? Class { get; set; }

    private string CssClass => string.Join(' ', new[]
    {
        "aetheus-loader",
        Centered ? "aetheus-loader-centered" : null,
        Class
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
