// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class PipelineRunStatusBadge
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineStatus Status { get; set; }

    /// <summary>Common minimum width so the badges of one column line up; Auto keeps the width of the content.</summary>
    [Parameter] public OmniBadgeWidth Width { get; set; } = OmniBadgeWidth.Auto;

    [Parameter] public OmniFill Fill { get; set; } = OmniFill.Solid;

    /// <summary>Extra attributes (title, data-testid, class) passed to the badge.</summary>
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AdditionalAttributes { get; set; }
}
