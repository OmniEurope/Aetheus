// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Auth;

public partial class Login
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    /// <summary>The account the public demo seeds and advertises (PUBLIC_ADMIN_PASSWORD of
    /// aetheus-demo).</summary>
    internal const string PublicDemoUsername = "admin";

    /// <summary>The demo password: public by design, shown only when the backend reports the public demo.</summary>
    internal const string PublicDemoPassword = "aetheus-demo";

    // The read-only fields bind to members, not constants: an OE input's ValueExpression must name a member.
    private string DemoUsername { get; } = PublicDemoUsername;
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
        _model.Username = PublicDemoUsername;
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
                Username = _model.Username,
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

    private class LoginModel
    {
        [Required]
        [StringLength(100)]
        public string Username { get; set; } = string.Empty;

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
