// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Front.Components.Auth;

/// <summary>R-1 (owner decision 2026-10-29): the seeded administrator of a fresh install chooses its holder's own
/// name, address and password. The server holds the account on this step whatever the page does.</summary>
public partial class AccountSetup
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private RealtimeSessionLifecycle RealtimeSession { get; set; } = default!;

    /// <summary>Preserve the return path through the new sign-in.</summary>
    [SupplyParameterFromQuery] public string? ReturnUrl { get; set; }

    private readonly AccountSetupModel _model = new();
    private bool _busy;
    private string? _error;

    private async Task LogoutAsync()
    {
        await Api.Auth.NotifyLogoutAsync(await Auth.CurrentRefreshTokenAsync());
        await StopRealtimeAndLogoutAsync();
        Nav.NavigateTo("/login");
    }

    private async Task OnSubmit()
    {
        _busy = true;
        _error = null;

        var status = await Api.Auth.SetUpOwnAccountAsync(new SetUpOwnAccountRequest
        {
            Username = _model.Username,
            Email = _model.Email,
            CurrentPassword = _model.CurrentPassword,
            NewPassword = _model.NewPassword
        });

        if (status == HttpStatusCode.NoContent)
        {
            // The setup rotated the stamp and ended every token: sign in again with the new address.
            Toast.Success("Saved", "AccountSetupDone");
            await StopRealtimeAndLogoutAsync();
            Nav.NavigateTo(LoginRedirect.WithReturnUrl(LoginRedirect.LoginPath, ReturnUrl));
            return;
        }

        _error = status == HttpStatusCode.Conflict ? L["AccountSetupConflict"].Value : L["AccountSetupRefused"].Value;
        _busy = false;
    }

    private async Task StopRealtimeAndLogoutAsync()
    {
        await RealtimeSession.StopAsync();
        await Auth.LogoutAsync();
    }
}
