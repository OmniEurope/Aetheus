// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Front.Components.Settings;

public partial class Settings : IDisposable
{
    [Parameter] public string? Tab { get; set; }

    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniAppearanceState Appearance { get; set; } = default!;
    [Inject] private IUserPreferences Preferences { get; set; } = default!;

    private string _displayName = string.Empty;
    private string _language = "en";
    private int _selectedTabIndex;
    private string _version = "dev";
    private string _userRole = string.Empty;

    private static readonly string[] TabNames = ["appearance", "notifications", "security", "tokens", "about"];

    // --- TOTP 2FA ---
    private bool _totpEnabled;
    private bool _totpBusy;
    private TotpSetupResponse? _totpSetup;
    private string _verifyCode = string.Empty;
    private string _disablePassword = string.Empty;

    // --- Self-service password change ---
    // S-UX-PW3K: form model carrying DataAnnotations so the change-password form gets inline,
    // localized validation (required / min length / confirmation match) like every other form.
    private readonly PasswordChangeModel _pwModel = new();
    private bool _pwBusy;

    private readonly record struct DropdownItem(string Label, string Value);

    private List<DropdownItem> _languages = [];

    // Settings navigates its tabs by PATH (/settings/{tab}), so each tab click is a real path change:
    // BreadcrumbService clears the breadcrumb and OnInitializedAsync does NOT re-run (same component,
    // route-param change). Re-set the (static) breadcrumb in OnParametersSet so it survives every tab
    // click - the central query-only guard in BreadcrumbService only covers UrlSyncedTabs pages.
    protected override void OnParametersSet()
    {
        Breadcrumb.Set(new BreadcrumbItem(L["Settings"]));
        // Recette R1-55: Back and Forward change only the {Tab} parameter of this same component, so the
        // selected tab follows it here, not only once at initialization.
        _selectedTabIndex = TabIndexOf(Tab);
    }

    private static int TabIndexOf(string? tab)
    {
        var index = Array.FindIndex(TabNames, name => name.Equals(tab, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }

    protected override async Task OnInitializedAsync()
    {
        _selectedTabIndex = TabIndexOf(Tab);

        _languages =
        [
            new(L["LanguageEnglish"], "en"),
            new(L["LanguageFrench"], "fr-FR")
        ];

        _userRole = Auth.IsAdmin ? "Admin" : "User";

        try
        {
            var me = await Api.Auth.GetCurrentUserAsync();
            _totpEnabled = me?.TotpEnabled ?? false;
        }
        catch (HttpRequestException) { /* graceful degradation */ }
        _version = Configuration["App:Version"] ?? "dev";

        // Display name is stored in browser localStorage; NotificationPreferencesPanel stores
        // notification preferences on the server.
        var storedName = await JS.InvokeAsync<string>("localStorage.getItem", StorageKeys.DisplayName);
        _displayName = string.IsNullOrWhiteSpace(storedName) ? (Auth.Username ?? string.Empty) : storedName;

        // The look is read from the one source the Theme window shares.
        Appearance.Changed += OnAppearanceChanged;
        await Appearance.LoadAsync();

        // The selector must mirror the culture ACTUALLY in effect (what the UI is really rendering),
        // not the raw localStorage value. index.html applies the saved language at boot via
        // Blazor.start({ applicationCulture }); if that ever diverges from localStorage (a legacy/
        // malformed stored value, or a boot where the culture failed to apply), reading localStorage
        // first would make the dropdown claim "French" while every string renders in English. Using
        // CurrentUICulture as the single source of truth keeps the dropdown and the rendered language
        // in sync by construction.
        var activeCulture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        _language = activeCulture.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr-FR" : "en";
    }

    // OE selects a tab by its key: each tab's Key is its path segment in TabNames.
    private async Task OnTabChange(string? key)
    {
        var index = Array.FindIndex(TabNames, name => name.Equals(key, StringComparison.Ordinal));
        if (index < 0) return;
        if (index == _selectedTabIndex) return;
        _selectedTabIndex = index;
        // Recette R1-55: a real history entry per tab, so Back and Forward walk the tabs visited.
        Navigation.NavigateTo($"/settings/{TabNames[index]}");
        // Settings navigates its tabs by PATH, so each tab click is a real path change and
        // BreadcrumbService (registered after the Router) clears the breadcrumb in the async tail of the
        // navigation - AFTER the routed OnParametersSet has re-set it, and after a synchronous re-set
        // here too. Yield so this re-set runs as a continuation AFTER that deferred clear, making it the
        // last write. (Query-tab pages via UrlSyncedTabs don't hit this: the central guard skips the
        // clear on a same-path query change.)
        await Task.Yield();
        Breadcrumb.Set(new BreadcrumbItem(L["Settings"]));
    }

    private void OnAppearanceChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Appearance.Changed -= OnAppearanceChanged;

    private async Task OnLanguageChangedAsync(object value)
    {
        _language = value?.ToString() ?? "en";
        await JS.InvokeVoidAsync("localStorage.setItem", StorageKeys.Lang, _language);
        // R1-66: the language is the user's, on every device; the browser key is its boot cache.
        await Preferences.SetAsync(PreferenceKeys.Language, _language);
        await JS.InvokeVoidAsync("Aetheus.setLang", _language);
        Toast.Success("Saved", "AppearanceSaved");
        Navigation.NavigateTo(Navigation.Uri, forceLoad: true);
    }

    // Submit handler - only fires once the LocalizedDataAnnotationsValidator reports the form valid,
    // so the imperative length/match guards are gone (they live in the model's annotations now).
    private async Task ChangePassword()
    {
        _pwBusy = true;
        var ok = await Api.Auth.ChangeOwnPasswordAsync(new ChangeUserPasswordRequest
        {
            CurrentPassword = _pwModel.CurrentPassword,
            NewPassword = _pwModel.NewPassword
        });
        if (ok)
        {
            Toast.Success("Saved", "PasswordChanged");
            _pwModel.CurrentPassword = _pwModel.NewPassword = _pwModel.ConfirmPassword = string.Empty;
        }
        else
        {
            Toast.Error("Error", "PasswordChangeFailed");
        }
        _pwBusy = false;
    }

    private async Task SetupTotp()
    {
        _totpBusy = true;
        try
        {
            _totpSetup = await Api.Auth.SetupTotpAsync();
            if (_totpSetup is not null)
                Toast.Info("TotpSetup", "TotpSetup");
            else
                Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _totpBusy = false;
        }
    }

    private async Task VerifyTotp()
    {
        if (string.IsNullOrWhiteSpace(_verifyCode)) return;
        _totpBusy = true;
        var ok = await Api.Auth.VerifyTotpAsync(_verifyCode);
        if (ok)
        {
            _totpEnabled = true;
            _totpSetup = null;
            _verifyCode = string.Empty;
            Toast.Success("Saved", "TotpEnabledSuccess");
        }
        else
        {
            Toast.Error("Error", "TotpVerifyFailed");
        }
        _totpBusy = false;
    }

    private async Task DisableTotp()
    {
        if (string.IsNullOrWhiteSpace(_disablePassword)) return;
        _totpBusy = true;
        var ok = await Api.Auth.DisableTotpAsync(_disablePassword);
        if (ok)
        {
            _totpEnabled = false;
            _disablePassword = string.Empty;
            Toast.Success("Saved", "TotpDisabledSuccess");
        }
        else
        {
            Toast.Error("Error", "TotpDisableFailed");
        }
        _totpBusy = false;
    }
}
