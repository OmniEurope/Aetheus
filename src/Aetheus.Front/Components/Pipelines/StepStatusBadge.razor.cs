// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Pipelines;

public partial class StepStatusBadge
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineStepRunDto Step { get; set; } = default!;
    [Parameter] public string? Class { get; set; }

    private OmniTone BadgeVariant => PipelineRunFormatting.GetStepBadge(Step) switch
    {
        OmniTone.Success => OmniTone.Success,
        OmniTone.Warning => OmniTone.Warning,
        OmniTone.Danger => OmniTone.Danger,
        OmniTone.Accent => OmniTone.Accent,
        _ => OmniTone.Neutral
    };

    /// <summary>Recette R-24: every failure is a filled red badge, a non-blocking one included (its tooltip
    /// says it did not block); only the advisory badge beside it stays an outline.</summary>
    private static OmniFill BadgeFill => OmniFill.Solid;

    /// <summary>The badge's icon fragment: every status has one (OE's Icon is a fragment).</summary>
    private RenderFragment StatusIcon => BadgeIcon.Of(PipelineRunFormatting.StepBadgeIcon(Step));
}
