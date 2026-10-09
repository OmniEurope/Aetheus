// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>A checked line: green (passes), red (to fix) or grey (not checked yet).</summary>
public partial class MailCheckLine
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public string Label { get; set; } = string.Empty;

    /// <summary>True passes, false is to fix, null is not checked yet.</summary>
    [Parameter] public bool? IsOk { get; set; }

    [Parameter] public string? Detail { get; set; }

    [Parameter] public string? Class { get; set; }

    private string State => IsOk switch { true => "ok", false => "fail", _ => "unchecked" };

    private OmniTone Tone => IsOk switch { true => OmniTone.Success, false => OmniTone.Danger, _ => OmniTone.Neutral };

    private string BadgeText => IsOk switch
    {
        true => L["MailVerdictOk"],
        false => L["MailStatusToFix"],
        _ => L["MailNotChecked"]
    };
}
