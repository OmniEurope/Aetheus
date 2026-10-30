// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Auth;

public partial class Login
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    /// <summary>The sign-in e-mail of the account the public demo seeds and advertises: the seed's default
    /// administrator address, aetheus-demo setting no Auth:AdminEmail (PUBLIC_ADMIN_PASSWORD of
    /// aetheus-demo is its password).</summary>
    internal const string PublicDemoEmail = "admin@aetheus.local";

    /// <summary>The demo password: public by design, shown only when the backend reports the public demo.</summary>
    internal const string PublicDemoPassword = "aetheus-demo";

    // The read-only fields bind to members, not constants: an OE input's ValueExpression must name a member.
    private string DemoEmail { get; } = PublicDemoEmail;
    private string DemoPassword { get; } = PublicDemoPassword;

    /// <summary>The return path validated by <see cref="LoginRedirect"/>.</summary>
    [SupplyParameterFromQuery] public string? ReturnUrl { get; set; }

    /// <summary>The forgotten-password hint is open (<c>?forgot=true</c>), set by the footer
    /// link.</summary>
    [SupplyParameterFromQuery(Name = "forgot")] public bool Forgot { get; set; }

    private LoginModel _model = new();
    private string? _error;
    private bool _loading;
    private bool _totpRequired;
    private bool _useRecoveryCode;
    private bool _isPublicDemo;

    /// <summary>The current address with the hint toggled; the return URL and every other parameter are kept.</summary>
    private string ForgotPasswordHref => Nav.GetUriWithQueryParameter("forgot", Forgot ? null : (bool?)true);

    private void ToggleRecoveryCode()
    {
        _useRecoveryCode = !_useRecoveryCode;
        if (_useRecoveryCode)
            _model.TotpCode = null;
        else
            _model.RecoveryCode = null;
    }

    /// <summary>Writes the demo account into the sign-in fields; the user still presses
    /// Login.</summary>
    private void FillWithPublicDemoAccount()
    {
        _model.Email = PublicDemoEmail;
        _model.Password = PublicDemoPassword;
    }

    /// <summary>Show the application warning when the browser refuses clipboard access.</summary>
    private void OnDemoValueCopied(bool copied)
    {
        if (!copied)
            Toast.Warning("CopyFailed", "ClipboardUnavailable");
    }

    protected override async Task OnInitializedAsync()
    {
        if (Auth.IsAuthenticated)
        {
            Nav.NavigateTo(LoginRedirect.Resolve(ReturnUrl));
            return;
        }

        try
        {
            _isPublicDemo = (await Api.Auth.GetPublicDemoInfoAsync())?.Enabled == true;
        }
        catch (HttpRequestException)
        {
            _isPublicDemo = false;
        }
    }

    private async Task OnSubmit()
    {
        _loading = true;
        _error = null;

        try
        {
            var outcome = await Api.Auth.LoginAsync(new LoginRequest
            {
                Email = _model.Email,
                Password = _model.Password,
                RememberMe = _model.RememberMe,
                TotpCode = _totpRequired && !_useRecoveryCode ? _model.TotpCode : null,
                RecoveryCode = _totpRequired && _useRecoveryCode ? _model.RecoveryCode : null
            });

            if (outcome.Value is { } result)
            {
                // F-010: server signals TOTP is required - show the code input
                if (result.TotpRequired && string.IsNullOrEmpty(result.Token))
                {
                    _totpRequired = true;
                    return;
                }

                await Auth.LoginAsync(result.Token, result.RefreshToken);
                // Forced first-login password change: route straight to the mandatory screen and
                // skip the permissions prefetch (the user can't reach any gated page yet anyway).
                if (result.MustChangePassword)
                {
                    Nav.NavigateTo(LoginRedirect.WithReturnUrl(LoginRedirect.ChangePasswordPath, ReturnUrl));
                    return;
                }

                Nav.NavigateTo(LoginRedirect.Resolve(ReturnUrl));
            }
            else
            {
                _error = outcome.StatusCode switch
                {
                    System.Net.HttpStatusCode.TooManyRequests => L["LoginRateLimited"].Value,
                    >= System.Net.HttpStatusCode.InternalServerError => L["LoginUnavailable"].Value,
                    _ => L["InvalidCredentials"].Value
                };
            }
        }
        catch (HttpRequestException)
        {
            _error = L["NetworkError"].Value;
        }
        finally
        {
            _loading = false;
        }
    }

    private readonly ForgotPasswordModel _forgot = new();
    private bool _forgotBusy;
    private bool _resetRequested;
    private string? _forgotError;

    /// <summary>R-14: asks for a forgotten-password link. The server answers the same whether an account holds
    /// the address or not, so the page says the same too; only a refused request (too many) is said apart.</summary>
    private async Task OnForgotSubmit()
    {
        _forgotBusy = true;
        _forgotError = null;
        try
        {
            if (await Api.Auth.RequestPasswordResetAsync(new RequestPasswordResetRequest { Email = _forgot.Email.Trim() }))
            {
                _resetRequested = true;
                // Neutral on purpose: the same words whether an account holds the address or not.
                Toast.Info("ForgotPassword", "ForgotPasswordSent");
            }
            else
                _forgotError = L["ForgotPasswordRefused"].Value;
        }
        catch (HttpRequestException)
        {
            _forgotError = L["NetworkError"].Value;
        }
        finally
        {
            _forgotBusy = false;
        }
    }

    private sealed class ForgotPasswordModel
    {
        [Required]
        [StringLength(200)]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;
    }

    private class LoginModel
    {
        // PLAN-003 P3-2: people sign in by e-mail address.
        [Required]
        [StringLength(320)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }

        [StringLength(10)]
        public string? TotpCode { get; set; }

        [StringLength(20)]
        public string? RecoveryCode { get; set; }
    }
}
