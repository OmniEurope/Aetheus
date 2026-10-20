// SPDX-License-Identifier: EUPL-1.2

using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class ReleaseStatusBadge
{
    private OmniTone Variant => Status switch
    {
        ReleaseStatus.Detected or ReleaseStatus.Promoted => OmniTone.Accent,
        ReleaseStatus.Building => OmniTone.Warning,
        ReleaseStatus.Published or ReleaseStatus.Deployed => OmniTone.Success,
        ReleaseStatus.Failed => OmniTone.Danger,
        _ => OmniTone.Neutral
    };

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public ReleaseStatus Status { get; set; }
    [Parameter] public string? Class { get; set; }

    /// <summary>Only Superseded, Deployed (D38) and Archived (D2: what was deleted, what stays) carry an
    /// explanation; the others read for themselves.</summary>
    private string? Help => Status is ReleaseStatus.Superseded or ReleaseStatus.Deployed or ReleaseStatus.Archived
        ? L[$"ReleaseStatusHelp_{Status}"].Value
        : null;
}
