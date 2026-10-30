// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Servers;

public partial class AgentCompatibilityBadge
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Parameter] public AgentCompatibilityDto? Value { get; set; }

    /// <summary>The icons of the status counts in the Servers page header: Upgrade for a recommended update.</summary>
    private static OmniIconName IconFor(AgentCompatibilityStatus status) => status switch
    {
        AgentCompatibilityStatus.UpToDate => OmniIconName.Check,
        AgentCompatibilityStatus.UpdateRecommended => OmniIconName.Upgrade,
        AgentCompatibilityStatus.UpdateRequired => OmniIconName.Warning,
        _ => OmniIconName.Question
    };

    private static OmniTone BadgeVariantFor(AgentCompatibilityStatus status) => status switch
    {
        AgentCompatibilityStatus.UpToDate => OmniTone.Success,
        AgentCompatibilityStatus.UpdateRecommended => OmniTone.Warning,
        AgentCompatibilityStatus.UpdateRequired => OmniTone.Danger,
        _ => OmniTone.Neutral
    };
}
