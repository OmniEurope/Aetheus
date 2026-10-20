// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Mail;

/// <summary>Frames <see cref="MailClientSettings"/> as a card, or as a dialog with its close
/// button.</summary>
public partial class MailClientSettingsHelp
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter] public string? Email { get; set; }
    [Parameter] public bool InDialog { get; set; }
}
