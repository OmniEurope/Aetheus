// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

/// <summary>The outcome of a set-password mail (R-14), in the colour of what happened.</summary>
public partial class PasswordLinkMailAlert
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public WelcomeMailResultDto Result { get; set; } = default!;

    private OmniSeverity Severity => Result.Status switch
    {
        WelcomeMailStatus.Sent => OmniSeverity.Success,
        WelcomeMailStatus.NotConfigured => OmniSeverity.Warning,
        _ => OmniSeverity.Danger
    };

    private string CssClass => "password-link-mail password-link-mail-" + Result.Status.ToString().ToLowerInvariant();

    private string Text => Result.Status switch
    {
        WelcomeMailStatus.Sent => string.Format(L["PasswordLinkMailSent"].Value, Result.To),
        WelcomeMailStatus.NotConfigured => string.Format(
            L[Result.Reason == WelcomeMailReason.NoPublicAddress ? "PasswordLinkMailNoPublicAddress" : "PasswordLinkMailNoSender"].Value,
            Result.To),
        _ => string.Format(L["PasswordLinkMailFailed"].Value, Result.To)
    };
}
