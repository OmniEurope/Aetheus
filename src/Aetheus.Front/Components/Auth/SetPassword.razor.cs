// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Auth;

/// <summary>
/// Recette R-14: the person who received a welcome mail chooses their password with the single-use token of
/// its link, then signs in with their e-mail address.
/// </summary>
public partial class SetPassword
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private readonly PasswordSetupModel _model = new();
    private string? _token;
    private bool _busy;
    private string? _error;

    protected override void OnInitialized() => _token = TokenOf(Nav.Uri);

    /// <summary>The token of the address's fragment (<c>#token=...</c>), or null when it carries none.</summary>
    internal static string? TokenOf(string uri)
    {
        var hash = uri.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0) return null;
        var prefix = PasswordSetupLink.TokenFragmentKey + "=";
        var token = uri[(hash + 1)..].Split('&').FirstOrDefault(part => part.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        return string.IsNullOrWhiteSpace(token) ? null : Uri.UnescapeDataString(token);
    }

    private async Task OnSubmit()
    {
        _busy = true;
        _error = null;
        var ok = await Api.Auth.CompletePasswordSetupAsync(new CompletePasswordSetupRequest
        {
            Token = _token!,
            NewPassword = _model.NewPassword
        });
        if (ok)
        {
            Toast.Success("Saved", "PasswordSetupDone");
            Nav.NavigateTo(LoginRedirect.LoginPath);
            return;
        }

        _error = L["PasswordSetupInvalidLink"].Value;
        _busy = false;
    }
}
